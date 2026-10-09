# MediaPager.Plugins.Actions.Downloads

Community action plugin that owns its download settings, ffmpeg engine, and activity jobs.
It implements the community `IDownloadProviderPlugin` contract and resolves titles through
loaded `IStreamProviderPlugin` implementations. The host supplies only generic plugin actions,
activity, settings storage, and loaded-plugin access.

Settings exposes one catalog selector for each media type that has catalogs. Transfers go to
the selected catalog's configured path; scratch files can use that same catalog folder or the
system temporary directory.

The action plugin and its shared interface are community-maintained; the host does not ship
or depend on either repository.
