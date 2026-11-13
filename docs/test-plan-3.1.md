# 3.1 manual test checklist

Automated tests cover the engine, the endpoints and the database (Core and Web suites). These are the things only a real run, a real browser or a real kill can show.
Use a throwaway data folder (`COMPRESSARR_DATA_DIR`) and a few small test videos.

## Videos lane
- [ ] Lanes page > **Add videos lane**: the card has no TV column, a "Videos lane" chip, **Track in History** visible without Advanced, and its own processing (collision = Rename).
- [ ] Drop `Holiday S01E02.mkv` and `Cake (2024).mkv` into its Input: the queue shows the camera icon for both, the report says **Video**.
- [ ] After the run both files are directly in the destination path (no folders), under their own names. Drop the same name again: `... (2).mkv`, the first is untouched.
- [ ] With FileBot, Sonarr and Radarr configured, the log has no FileBot lines and no Sonarr/Radarr line for this lane. With posters on, no TMDb request names these files.
- [ ] "Lands in" a Videos lane from a standard lane files that movie flat in the Videos destination.

## Track in History
- [ ] Errors only: a clean file leaves no row in the report, no change to savings, no notification; the Monitor live log still shows its lines; the log **file** has no file name and no sub-folder name.
- [ ] Errors only: a file that fails (rename the preset away) shows in the report, the log file keeps its lines, and the row stays in the Monitor queue as an error.
- [ ] None: no run number is used up, no report is made, no notification is sent, and the lane is not in a report of another lane's files.
- [ ] A successful encode's `HBdetails` file is not kept for these lanes even with "keep successful HandBrake logs" on.

## Processing per lane
- [ ] Untick "Use the global settings" on a lane: fields fill from Settings; the chip appears; Settings > Processing names the lane.
- [ ] Lane A = Maintain, lane B = Recycle (global Recycle): after a run A's sources are still there and B's are in the Recycle Bin.
- [ ] Re-tick the box: a confirmation if it differs, then the lane follows the global settings again. Duplicate a lane: the copy has the same own settings.

## Queue: priority and Change all
- [ ] Add a *Lower* rule for a show, drop 10 of its episodes and then a movie: the movie is ahead of all 10. The episodes keep their order.
- [ ] Add a rule after files are queued: nothing moves until **Apply to waiting files**. A file dragged by hand is not moved by a new arrival.
- [ ] Row menu > *Lower priority for this show* makes the rule; choosing it again sets it back to normal. A `{tmdb-123}` folder is matched by the id.
- [ ] **Change all...**: set a profile and a landing lane for all, then for one lane only; the file being encoded and an error row are unchanged; order is unchanged.

## Reports
- [ ] **Save** downloads the report; the downloaded file has no buttons. **Print** hides the buttons.
- [ ] Share > Copy link works on localhost / https and falls back to a prompt on plain http; *Email a summary* opens a mail draft with the numbers and no file names.
- [ ] **Save a copy without file names**: open it, every name/path/message is "Removed", counts and sizes are intact.
- [ ] Trash can on a file row: the row reads "Removed", the HandBrake detail log file is gone, History totals and run numbers are unchanged. Remove all problem files of a run: it leaves the unseen-errors list and the badges.
- [ ] A report from an imported pre-3.0 run: Save works, "without file names" is refused (it cannot be redacted).

## Videos as a stat
- [ ] After a run with one movie, one video and one episode: History's Files card and the Movies/TV/Videos card show three parts; `{video_count}` renders in a notification; `/api/v1/stats` has `videos`.
- [ ] Start 3.1 on a copy of a 3.0 data folder: History opens, the totals are unchanged, older runs show no videos.

## Crash journal (H7)
- [ ] Start an encode with Delete mode, kill Compressarr during the move/delete step (add a large library move to widen the window). On the next start the log says "Finishing '...'", nothing is encoded again, the original is dealt with as the setting says and the companions are moved.
- [ ] Kill it after the encode but before the file is routed (disconnect the destination share, let the move fail, kill): the next start routes the waiting encode.
- [ ] Open the queue database afterwards: no entry is left in the `FinishPending` state.
