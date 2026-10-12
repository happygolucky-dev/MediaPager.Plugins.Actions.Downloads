# MediaPager.Plugins.Actions.Downloads

Community action plugin that owns its download settings and activity jobs. It runs in a
separate worker process, resolves titles through the host's stream-provider broker, and
requests typed transfers from the host media-tool broker. It never receives catalog paths,
FFmpeg command arguments, or executable paths.

Settings exposes a catalog selector for each media type, video/audio transcode profiles,
and a scratch-file placement option. The host resolves the selected catalog path and owns all
file writes and activity updates. Its sandboxed settings iframe reads only the current
catalog IDs, names, and media types through the host bridge; it does not receive the API token
or catalog filesystem paths. Easy mode uses the recommended video/audio defaults. Advanced
mode exposes friendly dropdowns for choosing alternate output formats.

Movie and other non-TV stream items can be queued from poster cards or detail screens. TV
downloads are offered from the TV detail screen: individual episodes have their own action,
season and series actions confirm before queuing, and each selected episode is resolved and
transferred sequentially. TV episode metadata is saved under its series and season in the
destination catalog.

FFmpeg 9.0.2 and FFprobe are provisioned on first use into the app-managed tools cache
(`Tools:Directory/ffmpeg/9.0.2/<runtime-id>`). Platform archives are SHA-256 checked; the
host does not use or modify the OS `PATH`.

The action plugin and its shared interface are community-maintained; the host does not ship
or depend on either repository.
