# Orleans.Redis GIG fork

Made to quickly be able to make changes to the Orleans Redis provider while contributing back to the base repo via PR's.

## Making changes

`git checkout dev` - or whichever branch is the active one, for 2.0.0 this is currently dev
`git checkout -b feature`

Make changes

`git add .`
`git commit -m "Commit message"`
`git push -u origin feature`

Make PR to base repo, `feature`-branch can be merged into gig-dev where you can run the build-script to publish an updated version.
Remember to bump the `buildNumber` in `build.cake`.

## License

MIT
