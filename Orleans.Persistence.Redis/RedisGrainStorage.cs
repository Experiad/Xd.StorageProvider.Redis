using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Serialization;
using StackExchange.Redis;
using Orleans.Storage;

namespace Orleans.Persistence
{
    public class RedisGrainStorage : IGrainStorage, ILifecycleParticipant<ISiloLifecycle>
    {
        private static ConnectionMultiplexer _connection;
        private static SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

        private const string _writeScript = "local etag = redis.call('HGET', @key, 'etag')\nif etag == false or etag == @etag then return redis.call('HMSET', @key, 'etag', @newEtag, 'data', @data) else return false end";

        private readonly string _serviceId;
        private readonly string _name;
        private readonly SerializationManager _serializationManager;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger _logger;
        private readonly RedisStorageOptions _options;

        private JsonSerializerSettings _jsonSettings { get; } = new JsonSerializerSettings()
        {
            TypeNameHandling = TypeNameHandling.All,
            PreserveReferencesHandling = PreserveReferencesHandling.Objects,
            DateFormatHandling = DateFormatHandling.IsoDateFormat,
            DefaultValueHandling = DefaultValueHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            NullValueHandling = NullValueHandling.Ignore,
            ConstructorHandling = ConstructorHandling.AllowNonPublicDefaultConstructor,
        };

        private ConfigurationOptions _redisOptions;
        private LuaScript _preparedWriteScript;

        public RedisGrainStorage(
            string name, 
            RedisStorageOptions options, 
            SerializationManager serializationManager,
            IOptions<ClusterOptions> clusterOptions, 
            ILoggerFactory loggerFactory
        )
        {
            this._name = name;

            this._loggerFactory = loggerFactory;
            var loggerName = $"{typeof(RedisGrainStorage).FullName}.{name}";
            this._logger = loggerFactory.CreateLogger(loggerName);

            this._options = options;
            this._serializationManager = serializationManager;
            this._serviceId = clusterOptions.Value.ServiceId;
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
            var name = OptionFormattingUtilities.Name<RedisGrainStorage>(_name);
            lifecycle.Subscribe(name, _options.InitStage, Init, Close);
        }
        
        /// <summary> Initialization function for this storage provider. </summary>
        /// <see cref="IProvider#Init"/>
        public async Task Init(CancellationToken cancellationToken)
        {
            var timer = Stopwatch.StartNew();

            try
            {
                var initMsg = string.Format("Init: Name={0} ServiceId={1} DatabaseNumber={2} UseJson={3} DeleteOnClear={4}",
                        _name, _serviceId, _options.DatabaseNumber, _options.UseJson, _options.DeleteOnClear);
                _logger.LogInformation($"RedisGrainStorage {_name} is initializing: {initMsg}");

                _redisOptions = ConfigurationOptions.Parse(_options.DataConnectionString);
                if (_connection == null)
                    _connection = await Perform("Init", 5, () => ConnectionMultiplexer.ConnectAsync(_redisOptions), delayMs: 500).ConfigureAwait(false);
                
                _preparedWriteScript = LuaScript.Prepare(_writeScript);

                var loadTasks = new Task[_redisOptions.EndPoints.Count];
                for (int i = 0; i < _redisOptions.EndPoints.Count; i++)
                {
                    var endpoint = _redisOptions.EndPoints.ElementAt(i);
                    var server = _connection.GetServer(endpoint);

                    loadTasks[i] = _preparedWriteScript.LoadAsync(server);
                }
                await Task.WhenAll(loadTasks).ConfigureAwait(false);

                timer.Stop();
                _logger.LogInformation("Init: Name={0} ServiceId={1}, initialized in {2} ms",
                    _name, _serviceId, timer.Elapsed.TotalMilliseconds.ToString("0.00"));
            }
            catch (Exception ex)
            {
                timer.Stop();
                _logger.LogError(ex, "Init: Name={0} ServiceId={1}, errored in {2} ms. Error message: {3}",
                    _name, _serviceId, timer.Elapsed.TotalMilliseconds.ToString("0.00"), ex.Message);
                throw;
            }
        }

        private async Task<IDatabase> GetDatabase()
        {
            if (!_connection.IsConnected)
            {
                // Acquire lock to reconnect, or wait until it has reconnected
                await _semaphore.WaitAsync();
                // Might have changed since the lock was acquired, check again
                if (!_connection.IsConnected)
                {
                    try
                    {
                        _connection.Dispose();
                        _connection = null;
                        _connection = await Perform("Init", 5, () => ConnectionMultiplexer.ConnectAsync(_redisOptions), delayMs: 500).ConfigureAwait(false);
                    }
                    catch
                    {
                        _semaphore.Release();
                        throw;
                    }
                }
                _semaphore.Release();
            }
            if (_options.DatabaseNumber.HasValue)
                return _connection.GetDatabase(_options.DatabaseNumber.Value);
            else
                return _connection.GetDatabase();
        }

        private async Task Perform(string name, int retries, Func<Task> operation)
        {
            Exception exception = null;
            for (int i = 0; i < retries; i++)
            {
                try
                {
                    await operation().ConfigureAwait(false);
                    return;
                }
                catch (Exception ex)
                {
                    exception = ex;
                    _logger.LogError(ex, "Perform - {0}: Name={1} ServiceId={2}, Try={3}, Error message: {4}",
                        name, _name, _serviceId, i, ex.Message);
                    await Task.Delay(100).ConfigureAwait(false);
                }
            }
            throw new Exception($"Perform - {name} - couldn't complete operation in {retries} tries. See InnerException", exception);
        }

        public async Task<T> Perform<T>(string name, int retries, Func<Task<T>> operation, int delayMs = 100)
        {
            Exception exception = null;
            for (int i = 0; i < retries; i++)
            {
                try
                {
                    return await operation().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // This is nasty, but this exception is thrown when trying read using Hash, older
                    // versions of the provider didn't support ETag and wrote the state into a simple key
                    if (ex is RedisServerException && ex.Message.Contains("WRONGTYPE"))
                        throw;
                    exception = ex;
                    _logger.LogError(ex, "Perform - {0}: Name={1} ServiceId={2}, Try={3}, Error message: {4}",
                        name, _name, _serviceId, i, ex.Message);
                    await Task.Delay(delayMs).ConfigureAwait(false);
                }
            }
            throw new Exception($"Perform - {name} - couldn't complete operation in {retries} tries. See InnerException", exception);
        }

        /// <summary> Read state data function for this storage provider. </summary>
        /// <see cref="IStorageProvider#ReadStateAsync"/>
        public async Task ReadStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            var timer = Stopwatch.StartNew();

            var key = GetKey(grainReference);

            var db = await GetDatabase().ConfigureAwait(false);
            try
            {
                var hashEntries = await Perform("Reading", 3, () => db.HashGetAllAsync(key)).ConfigureAwait(false);
                if (hashEntries.Length == 2)
                {
                    var etagEntry = hashEntries.Single(e => e.Name == "etag");
                    var valueEntry = hashEntries.Single(e => e.Name == "data");
                    if (_options.UseJson)
                        grainState.State = JsonConvert.DeserializeObject(valueEntry.Value, grainState.State.GetType(), _jsonSettings);
                    else
                        grainState.State = _serializationManager.DeserializeFromByteArray<object>(valueEntry.Value);
                    grainState.ETag = etagEntry.Value;
                } else
                {
                    grainState.ETag = Guid.NewGuid().ToString();
                }
                timer.Stop();
                _logger.LogInformation("Reading: GrainType={0} Pk={1} Grainid={2} ETag={3} from Database={4}, finished in {5} ms",
                    grainType, key, grainReference, grainState.ETag, db.Database, timer.Elapsed.TotalMilliseconds.ToString("0.00"));
            }
            catch (RedisServerException)
            {
                var stringValue = await Perform("Reading", 3, () => db.StringGetAsync(key)).ConfigureAwait(false);
                if (stringValue.HasValue)
                {
                    if (_options.UseJson)
                        grainState.State = JsonConvert.DeserializeObject(stringValue, grainState.State.GetType(), _jsonSettings);
                    else
                        grainState.State = _serializationManager.DeserializeFromByteArray<object>(stringValue);
                }
                grainState.ETag = Guid.NewGuid().ToString();
                timer.Stop();
                _logger.LogInformation("Reading: GrainType={0} Pk={1} Grainid={2} ETag={3} from Database={4}, finished in {5} ms (migrated old Redis data, grain now supports ETag)",
                    grainType, key, grainReference, grainState.ETag, db.Database, timer.Elapsed.TotalMilliseconds.ToString("0.00"));
            }
        }

        /// <summary> Write state data function for this storage provider. </summary>
        /// <see cref="IStorageProvider#WriteStateAsync"/>
        public async Task WriteStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            var timer = Stopwatch.StartNew();
            var key = GetKey(grainReference);

            RedisResult response = null;
            var newEtag = Guid.NewGuid().ToString();
            var db = await GetDatabase().ConfigureAwait(false);
            if (_options.UseJson)
            {
                var payload = JsonConvert.SerializeObject(grainState.State, _jsonSettings);
                var args = new { key, etag = grainState.ETag ?? "null", newEtag, data = payload };
                response = await Perform("Writing", 3, () => db.ScriptEvaluateAsync(_preparedWriteScript, args)).ConfigureAwait(false);
            }
            else
            {
                var payload = _serializationManager.SerializeToByteArray(grainState.State);
                var args = new { key, etag = grainState.ETag ?? "null", newEtag, data = payload };
                response = await Perform("Writing", 3, () => db.ScriptEvaluateAsync(_preparedWriteScript, args)).ConfigureAwait(false);
            }

            if (response.IsNull)
            {
                timer.Stop();
                _logger.LogError("Writing: GrainType={0} PrimaryKey={1} Grainid={2} ETag={3} to Database={4}, finished in {5} ms - Error: ETag mismatch!",
                    grainType, key, grainReference, grainState.ETag, db.Database, timer.Elapsed.TotalMilliseconds.ToString("0.00"));
                throw new InconsistentStateException($"ETag mismatch - tried with ETag: {grainState.ETag}");
            }

            grainState.ETag = newEtag;

            timer.Stop();
            _logger.LogInformation("Writing: GrainType={0} PrimaryKey={1} Grainid={2} ETag={3} to Database={4}, finished in {5} ms",
                grainType, key, grainReference, grainState.ETag, db.Database, timer.Elapsed.TotalMilliseconds.ToString("0.00"));
        }

        /// <summary> Clear state data function for this storage provider. </summary>
        /// <remarks>
        /// </remarks>
        /// <see cref="IStorageProvider#ClearStateAsync"/>
        public async Task ClearStateAsync(string grainType, GrainReference grainReference, IGrainState grainState)
        {
            var timer = Stopwatch.StartNew();
            var key = GetKey(grainReference);

            var db = await GetDatabase().ConfigureAwait(false);
            await Perform("Clearing", 3, () => db.KeyDeleteAsync(key)).ConfigureAwait(false);

            timer.Stop();
            _logger.LogInformation("Clearing: GrainType={0} Pk={1} Grainid={2} ETag={3} to Database={4}, finished in {5} ms",
                grainType, key, grainReference, grainState.ETag, db.Database, timer.Elapsed.TotalMilliseconds.ToString("0.00"));
        }

        private string GetKey(GrainReference grainReference)
        {
            var format = _options.UseJson ? "json" : "binary";
            return $"{grainReference.ToKeyString()}|{format}";
        }

        public Task Close(CancellationToken cancellationToken)
        {
            // _connection.Dispose();
            _logger.LogInformation("Close: Name={0} ServiceId={1}", _name, _serviceId);
            return Task.CompletedTask;
        }
    }
}
