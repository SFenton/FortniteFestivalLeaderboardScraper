---
status: canonical
owner: service
last_verified: 2026-10-03
last_verified_commit: 1c4c21a6
sources:
  - FSTService/Api/FeedbackEndpoints.cs
  - FSTService/FeedbackOptions.cs
  - FSTService/FeatureOptions.cs
  - FSTService/Api/FeatureEndpoints.cs
  - FSTService/Feedback/
  - FSTService/Program.cs
  - FSTService/Dockerfile
  - FSTService.Tests/Unit/Feedback/
  - FSTService.Tests/Integration/FeedbackEndpointIntegrationTests.cs
update_triggers:
  - A feedback route, form field, error code, size limit, media conversion rule, issue body layout, label, or GitHub integration changes.
---

# In-app feedback

Web and native apps file bug reports and feature requests from App Settings
without visiting GitHub. FSTService accepts the form and media, prepares the
media for GitHub, and files an issue in the configured tracker repository.

## Availability

The feature is off unless both are true:

- `Features:Feedback=true`;
- `Feedback:GitHubRepository` (`owner/name`) and `Feedback:GitHubToken` are
  set.

`GET /api/features` reports `feedback: true` only when both hold, so clients
hide the Settings entries otherwise. While unavailable, both feedback routes
return `404` with `code: "feedback_disabled"`. The token needs Issues
read/write on the tracker repository (and must be able to upload user
attachments for media). It is a secret: supply it through the production
environment, never a tracked file.

The public service role owns these routes. During rollout read-only startup
the existing guard rejects all non-GET requests, including submissions.

## Routes

Both routes are `AdminPrivate` (not publication data) and send
`Cache-Control: no-store`.

### `POST /api/feedback`

`multipart/form-data` with these fields:

| Field | Required | Notes |
|---|---|---|
| `kind` | yes | `bug` or `feature` |
| `platform` | yes | `web`, `ios`, `ipados`, `macos`, `iphone-duo`, `android`, `windows` |
| `title` | yes | max 200 chars; leading `[Bug]`/`[Feature]` tags are replaced by the kind's prefix |
| `description` | yes | max 10,000 chars |
| `repro` | bug only | max 10,000 chars; ignored for features |
| `expected` | bug only | max 10,000 chars; ignored for features |
| `appVersion` | no | max 64 chars |
| `clientInfo` | no | max 256 chars, such as OS/browser/device |
| `media` | no | repeated file part, at most `MaxAttachments` (4) images/videos |

Responses:

| Status | Body |
|---|---|
| `202` | `{ "id": "<32 hex>", "status": "queued" }` |
| `400` | `{ "error", "code" }`; `code` is `invalid_form`, `invalid_kind`, `invalid_platform`, `title_required`, `description_required`, `field_too_long`, `too_many_attachments`, or `unsupported_media` |
| `404` | `feedback_disabled` |
| `413` | `{ "error", "code": "payload_too_large", "maxBytes" }` (whole request over `MaxRequestBytes`, 90 MiB) |
| `429` | rate limited: `SubmissionsPerWindow` (60) per `SubmissionWindowMinutes` (60) per client IP |
| `503` | `feedback_busy` with `Retry-After: 60` when more than `MaxQueuedSubmissions` (10) are pending |

The body is streamed to a per-submission scratch directory; it is never
buffered in memory. File parts other than `media` are ignored.

### `GET /api/feedback/{id}`

Returns processing status for polling:

```json
{
  "id": "…",
  "status": "queued | processing | submitted | failed",
  "issueNumber": 123,
  "error": "user-safe message when failed",
  "attachments": [
    { "name": "clip.mov", "kind": "video", "outcome": "pending | attached | transcoded | skipped", "note": "…" }
  ]
}
```

`issueNumber`, `error`, and `note` are omitted when null. Status is kept in
memory for `StatusRetentionMinutes` (60) after a job finishes and is lost on
restart; unknown or malformed IDs return `404` with `code: "not_found"`. The
issue URL is not returned because the tracker repository may be private.

## Processing

One background consumer processes submissions in order.

1. Each attachment is identified by magic bytes, never by its client-supplied
   name or content type. Non-media files are skipped.
2. Location and device metadata is removed: JPEG APP1/APP13/comments and PNG
   text/EXIF chunks are stripped losslessly (EXIF orientation is applied
   first), and every ffmpeg output uses `-map_metadata -1`.
3. Media is fit to `GitHubAttachmentMaxBytes` (10 MiB):
   - PNG/JPEG/GIF within the limit upload unchanged after stripping;
   - oversized images, WebP, BMP, TIFF, and HEIC/HEIF become JPEG, stepping
     down from 4096 px to 960 px until they fit;
   - oversized GIFs become MP4;
   - MP4/MOV/WebM within the limit are remuxed without re-encoding;
   - other or oversized videos are re-encoded to H.264/AAC MP4 at a bitrate
     sized to the limit (250 kbps-4 Mbps video, scaled to 640-1920 px). Only
     when even the minimum bitrate cannot fit is the video trimmed, and the
     issue notes the kept length.
4. Prepared files are uploaded as GitHub user attachments.
5. The issue is created with the `[Bug]`/`[Feature]` title, a body using the
   tracker's issue-form headings (with the submitting platform's checkbox
   ticked), embedded media, per-file notes, and app version/client details.
   The body's exact last line is the untrusted-submission marker
   `<!-- fst-feedback:v1 -->`. Issues are filed with the owner's token, so the
   tracker's triage relies on this line alone to treat the issue as anonymous
   app text rather than owner-written instructions.
6. User-supplied text (the title, every form field, and attachment file names)
   is neutralized before it is written: `<!--` becomes `&lt;!--` and `-->`
   becomes `--&gt;`, so users can neither hide content in HTML comments nor
   forge the marker, and `@` mentions are broken with a zero-width space.
   The appended marker is therefore the only HTML comment in the body.
7. The issue is labeled `From App` plus the plain label that `PlatformLabels`
   maps the submitting platform to (`web`→`Web`, `ios`→`iOS`,
   `iphone-duo`→`iPhone Duo`, `ipados`→`iPadOS`, `macos`→`macOS`,
   `android`→`Android`, `windows`→`Windows`). The tracker does not use
   `prefix:value` labels, so no `surface:*` label is applied. A platform with
   no or an empty map entry gets only `From App`. If GitHub rejects the labels,
   the issue is filed without them; the marker still identifies it.

An attachment that cannot be converted or uploaded is skipped and listed in
the issue; the issue is still filed. If the issue cannot be created, the job
fails with a generic message and the details are logged server-side only.
Scratch directories are deleted after each job and stale ones are removed at
startup.

ffmpeg runs with argument lists (no shell), a fixed demuxer per detected
format, `-protocol_whitelist file`, and a `TranscodeTimeoutSeconds` (240)
kill timeout. The runtime image installs `ffmpeg` (which provides `ffprobe`)
from Ubuntu packages.

## Configuration

Section `Feedback` (see `FSTService/appsettings.json`):

| Key | Default | Purpose |
|---|---|---|
| `GitHubRepository` | empty | tracker `owner/name` |
| `GitHubToken` | empty | secret token; set via environment only |
| `GitHubApiBaseUrl` | `https://api.github.com` | REST base |
| `GitHubUploadsBaseUrl` | `https://uploads.github.com` | user-attachment upload base |
| `PlatformLabels` | `web`→`Web`, `ios`→`iOS`, `iphone-duo`→`iPhone Duo`, `ipados`→`iPadOS`, `macos`→`macOS`, `android`→`Android`, `windows`→`Windows` | platform → plain issue label; an empty value omits that platform's label (`From App` is always applied) |
| `MaxRequestBytes` | 94371840 | whole-request cap |
| `MaxAttachments` | 4 | files per submission |
| `GitHubAttachmentMaxBytes` | 10485760 | per-file target after conversion |
| `SubmissionsPerWindow` / `SubmissionWindowMinutes` | 60 / 60 | per-IP submit rate limit; high enough for filing many reports in a row, still bounds scripted floods on this unauthenticated endpoint |
| `MaxQueuedSubmissions` | 10 | pending job cap |
| `StatusRetentionMinutes` | 60 | status and stale-scratch retention |
| `FfmpegPath` / `FfprobePath` | `ffmpeg` / `ffprobe` | tool paths |
| `TranscodeTimeoutSeconds` | 240 | per ffmpeg/ffprobe run |
| `ScratchDirectory` | `feedback-scratch` | relative paths resolve under `Scraper:DataDirectory` |

Enabling in production needs `Features__Feedback=true`,
`Feedback__GitHubRepository`, and `Feedback__GitHubToken` on the public
service role. Any reverse proxy in front of `/api/feedback` must allow request
bodies up to `MaxRequestBytes`.

## Known limits

- HEIC decoding depends on the image's ffmpeg build; failures skip the file.
- GitHub's user-attachment upload endpoint is not a documented REST API. If it
  changes, attachments are skipped and issues are still filed.
- Status is process-local; a restart loses in-flight jobs and their status.
