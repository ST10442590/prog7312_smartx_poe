# Feedback and Revisions Ledger

Lecturer feedback received on Parts 1 and 2, and the changes made in response
to each point. Marker comments are quoted verbatim. Where a criterion lost
marks without a written comment, that is stated rather than glossed over, and
any reading of why is labelled as inference.

**Module:** PROG7312 / AAPD7112 — Programming 3B / Advanced Application Development
**Student:** Thabang Thamae (ST10442590)

Part 1: 90/100. Part 2: 90/100.

---

## Part 1 — written feedback

### 1. "There is no registration or upload UI."

*App Functionality: Sensor Telemetry Data Ingestion and UI — 6/10, "Ingestion
page is present, but input forms (MAC address, zone location, category)
contain notable data validation bugs."*

**Cause.** `SmartX.Client/Pages/Devices.razor` was never written to disk. The
editor reported the file as saved and the solution compiled, because nothing
referenced the page except the navigation menu, which failed at runtime rather
than at build time. Part 1 therefore shipped without its device registration
form, file upload, and attachment listing.

**Change.** The page was rebuilt in full for Part 2: MAC address validation,
category and unit selection with cross-field rules, the deployment location
fields, and the config/photo/log upload with SHA-256 integrity hashing.

**Prevention.** Every file write is now verified by reading the file back
before the solution is rebuilt. This caught a second silent save failure
later, in `NavMenu.razor` (entry 7).

### 2. "The shared validation has no client side use."

*Same criterion.*

**Cause.** `SensorProfile.Validate()` was correctly placed in
`SmartX.Shared` so that both client and server could call it, and the API
did. The client never did — because the page that would have called it did
not exist. The shared method was architecture without a consumer.

**Change.** The rebuilt registration form calls the identical
`Validate()` method on every field change, so the errors a user sees while
typing are produced by the same code the API runs on submission. The browser
is no longer the only line of defence, and the two can no longer disagree.

### 3. "Telemetry is not checked against registered sensor category or unit."

*Same criterion.*

**Cause.** `/api/telemetry` confirmed the device was registered and then
ingested the packet using the unit supplied in the request. Nothing compared
that unit, or the packet's category, against the device's registered profile.
A node registered to report humidity could send a Celsius reading and it would
be archived, scored and charted under the wrong unit, with nothing to show the
two disagreed.

**Change.** The ingestion endpoint now loads the registered profile and
refuses the packet with `400 Bad Request` if either the unit or the category
contradicts it, naming both the registered value and the submitted one. A
packet that disagrees with its own registration is a misconfigured node, not a
reading, so it is rejected at the boundary rather than stored.

**Status.** Fixed in this submission, not in Part 2.

### 4. "API address is hard-coded in Program.cs"

*Gateway Startup Layout and API Integration — 9/10.*

**Cause.** `SmartX.Client/Program.cs` constructed its `HttpClient` with
`new Uri("http://localhost:5145")` written directly into the source. Pointing
the client at a different gateway meant editing and rebuilding it, and the
address was invisible to anyone reading configuration rather than code.

**Change.** The address now comes from
`SmartX.Client/wwwroot/appsettings.json`, which Blazor WebAssembly loads into
configuration at startup, falling back to the client's own origin if the
setting is absent so the application still boots.

**Status.** Fixed in this submission, not in Part 2.

### 5. "No upload UI, no encrytion"

*Media / Log File Upload Attachment — 6/10, "File uploader is present but
exhibits notable bugs when saving configs or uploading images to the backend
API."*

**Upload UI.** Addressed in Part 2 by the same fix as entry 1 — the upload
control lives on the registration page that was missing.

**Encryption.** Not addressed. Uploaded files are hashed with SHA-256 on
receipt, which protects integrity — it detects a file that has been altered —
but does not protect confidentiality, and the marker asked for encryption
specifically. The two are not interchangeable and this submission should not
claim otherwise. Encrypting attachments at rest with AES, with the key held
in configuration rather than in source, remains outstanding work.

### 6. "References are mostly accurate, with minor issues in citation format."

*Referencing and Citations — 4/5.*

**Change.** All references across the submission were consolidated into a
single IIE Harvard format and checked against one another for consistency of
author ordering, edition placement and date format. In-text citations were
added to the project report, which previously carried a reference list with
nothing citing it.

---

## Part 2 — no written feedback

Part 2 was returned with band selections only; the criterion comment boxes
were empty. Four criteria lost marks, and the entries below state what was
changed and, where no comment exists, label the reasoning as inference.

### 7. Main Menu Navigation and Active Pillar State — 25/30

Five marks were withheld inside the top band, with no comment. The criterion
is described as covering "overall working of everything together", and the
band immediately below reads "Some of Part 1 work is included or does not work
perfectly."

**Inference, not a quoted comment.** Two candidates. The first is Part 1's
missing registration and upload UI, which the Part 1 marker flagged twice and
which was only restored partway through Part 2. The second is that Part 2's
navigation menu displayed the third architectural pillar as a disabled item.

**Change.** All five modules are now active and reachable. Making the third
pillar live exposed a second silent save failure: `NavMenu.razor` refused
repeated writes with no error, and because a disabled `<span>` is valid
markup, nothing failed to compile. The file was corrected and verified by
reading it back.

### 8. Predictive Action and Suggestion Engine — 27/30

Three marks withheld inside the top band, no comment. A review of
`SuggestionEngine.cs` found three defects that would produce recommendations
no operator behaviour justified.

**Change.** First, an alert firing on a device was marking that device as
*attended*, suppressing genuinely neglected nodes from the proactive
recommendations. Attendance is now set by an operator looking at a device, not
by the device alarming. Second, two unrelated alerts arriving in the same
window were being recorded as a learned transition — coincidence stored as
behaviour. Only operator actions may now be the target of a learned
transition. Third, predictions were drawn from the most recent event, which
under load was almost always an alert rather than an operator action.
Transitions must now occur within three minutes and be observed at least twice
before being suggested.

### 9. Stacks and Queues — 14/15, and Dictionaries — 14/15

Both one band short, both without comment. The band above each describes the
same quality: "without latency" and "instantaneous". Read together these
indicate a performance and polish gap rather than a defect, and no specific
fault was identified by the marker or found on review. Recorded here for
completeness rather than claimed as addressed.

---

## Self-identified, no marker comment

### 10. Inconsistent code comment style

XML documentation comments (`///` with `<summary>` tags) were used in some
files and ordinary `//` comments in others, so the codebase read as though it
had been assembled from different sources. All comments were standardised on
`//`, explaining why a decision was made rather than restating what the line
does.

---

## Carried forward into this submission

**Verify the write, not the save.** Entries 1 and 7 were the same failure
twice, and the first one cost marks on three separate Part 1 criteria. Any
file that is created or changed is now read back before the solution is
rebuilt.

**Check the invariant, not the output.** Entry 8 was a set of bugs that
produced plausible-looking suggestions, and the same class of error appeared
again in this submission's implementation of Prim's algorithm, which produced
a spanning tree that was connected, acyclic, complete — and not minimal.
Neither was detectable by inspecting the result. The minimum spanning tree
endpoint therefore reports `vertices`, `edges` and `isComplete`, so the
structural guarantee that a spanning tree over *n* vertices has exactly
*n* − 1 edges can be checked on every run.

**Fix the feedback before adding features.** Entries 3 and 4 were named in
writing after Part 1 and were still present when work on this submission
began. Both have now been fixed, and both commits quote the marker's words in
the commit message so the version history records what prompted the change.
