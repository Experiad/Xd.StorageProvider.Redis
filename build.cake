///////////////////////////////////////////////////////////////////////////////
// ARGUMENTS
///////////////////////////////////////////////////////////////////////////////
#tool "nuget:?package=JetBrains.dotCover.CommandLineTools"
#tool "nuget:?package=xunit.runner.console"
#tool "nuget:?package=OctopusTools"
#addin "Cake.Docker"

var target = Argument<string>("target", "Default");
var configuration = Argument<string>("configuration", "Release");
var isOnTeamCity = TeamCity.IsRunningOnTeamCity;

///////////////////////////////////////////////////////////////////////////////
// GLOBAL VARIABLES
///////////////////////////////////////////////////////////////////////////////

var solutions = GetFiles("./*.sln");
var solutionPaths = solutions.Select(solution => solution.GetDirectory());
var solutionPath = solutions.First().GetDirectory();
var projects = GetFiles("./**/*.csproj");
var solution = solutions.First().FullPath;
    
var buildNumber = isOnTeamCity ? TeamCity.Environment.Build.Number : "1";
var version = "2.0.0." + buildNumber;

var nugetStore = Argument("nuget_store", default(string)) ?? EnvironmentVariable("nuget_store");
var nugetKey = Argument("nuget_key", default(string)) ?? EnvironmentVariable("nuget_key");

///////////////////////////////////////////////////////////////////////////////
// TASK DEFINITIONS
///////////////////////////////////////////////////////////////////////////////

Task("Restore")
    .Description("Restores all the NuGet packages that are used by the specified solution.")
    .Does(() =>
{
	var settings = new DotNetCoreRestoreSettings 
	{
		Verbosity = DotNetCoreVerbosity.Quiet
	};
	DotNetCoreRestore(solution, settings);
});

Task("Build")
    .Description("Builds all the different parts of the project.")
    .IsDependentOn("Restore")
    .Does(() =>
{
	var settings = new DotNetCoreBuildSettings
	{
		Configuration = "Debug",
		Verbosity = DotNetCoreVerbosity.Quiet,
        NoRestore = true
	};

     DotNetCoreBuild(solution, settings);
});

Task("Test")
    .IsDependentOn("Build")
    .Does(() => 
{
	DeleteFiles("./*.dcvr");
	var projects = GetFiles("./Tests/*.Tests/*.Tests.*");
	Parallel.ForEach(projects, project => 
	{
		var name = project.GetFilenameWithoutExtension();
		DotCoverCover(
			tool => 
			{
				var settings = new DotNetCoreTestSettings
				{
					Configuration = "Debug",
					NoBuild = true,
					NoRestore = true,
					Verbosity = DotNetCoreVerbosity.Quiet
				};
				tool.DotNetCoreTest(project.FullPath, settings);
			},
			new FilePath("./" + name + ".dcvr"),
			new DotCoverCoverSettings()
				.WithFilter("+:Orleans.*.Redis")
				.WithFilter("-:*.Test*")
		);
	});
	
	var files = GetFiles("./*.dcvr");
	DotCoverMerge(files, new FilePath("./result.dcvr"));
	DotCoverReport(
		new FilePath("./result.dcvr"),
		new FilePath("./result.html"),
		new DotCoverReportSettings {
			ReportType = DotCoverReportType.HTML
		}
	);
	TeamCity.ImportDotCoverCoverage(
		new FilePath("./result.dcvr"),
		MakeAbsolute(Context.Tools.Resolve("dotcover.exe").GetDirectory())
	);
});

Task("OnlyTest")
    .Does(() => 
{
	var settings = new DotNetCoreTestSettings
	{
		Configuration = "Debug",
		Verbosity = DotNetCoreVerbosity.Quiet
	};
	DotNetCoreTest(GetFiles("./Tests/*.Tests/*.csproj").First().FullPath, settings);
});

Task("NuGetPack")
	.IsDependentOn("Test")
	.Does(() => 
{
	if(!DirectoryExists("./nugetoutput")) 
	{
		CreateDirectory("./nugetoutput");
	}
	CleanDirectory("./nugetoutput");

	var nugetPath = "./nugetoutput"; 

	var settings = new DotNetCorePackSettings
	{
		Configuration = "Debug",
		OutputDirectory = nugetPath,
		NoBuild = true,
		NoRestore = true,
		Verbosity = DotNetCoreVerbosity.Quiet,
		ArgumentCustomization = args => args.Append("/p:PackageVersion=" + version)
	};

	foreach (var project in projects)
	{
		if (project.FullPath.Contains("Test"))
			return;
		DotNetCorePack(project.FullPath, settings);
	}
});


Task("NuGetPublish")
	.IsDependentOn("NuGetPack")
	.Does(() => 
{
    var packages = GetFiles("./nugetoutput/*.nupkg");

	// Push the package.
	NuGetPush(packages, new NuGetPushSettings {
		Source = nugetStore,
		ApiKey = nugetKey
	});
});

///////////////////////////////////////////////////////////////////////////////
// TARGETS
///////////////////////////////////////////////////////////////////////////////

Task("Default")
    .Description("This is the default task which will be ran if no specific target is passed in.")
    .IsDependentOn("NuGetPublish");

///////////////////////////////////////////////////////////////////////////////
// EXECUTION
///////////////////////////////////////////////////////////////////////////////

RunTarget(target);