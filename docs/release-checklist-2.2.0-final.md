# Before cutting 2.2.0 final

Things that are set for the betas and must change (or be done) at the final release. Beta builds do not need any of this.

1. **Update channel default -> Stable.** In `src/Compressarr.Core/Config/CompressarrConfig.cs` change
   `UpdateCheckSettings.DefaultChannel` from `UpdateChannel.Beta` to `UpdateChannel.Stable`. During the betas it is Beta so testers are offered
   each new beta. The test `UpdateChannelDefaultTests` fails as soon as the version has no pre-release tag (for example `2.2.0`) until
   this is done, so the version bump cannot ship without it. Update the changelog line ("During the 2.2 betas the default is...") and the
   wiki's *Updating Compressarr* page to say Stable is the default.
   (Installs that already saved a choice keep it - that is intended.)
2. **Wiki:** the 2.2 pages are written for 2.2 only (the 2.1 line retires soon after); publish the local wiki commits when 2.2 ships.
3. **Announcements:** post the "2.2 is out" message to the Compressarr-Announcements repo (aimed at 2.1 with `maxAppVersion`), and the
   matching 2.1.10 notice if it is released first.
4. **Test plan:** the items still marked not run (K15/K16 novice walkthrough on a clean machine, E2/E10/E11/E13, H12 scheduler priority,
   B17 ffmpeg channel).
5. **Website / README:** update for 2.2.
