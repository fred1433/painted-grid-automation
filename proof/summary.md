| Scenario | What it checks | Result | Checks |
|---|---|---|---|
| `01-protocol-surface` | tools/list, annotations, structured errors, stdout carries JSON-RPC only | **PASS** | 11/11 |
| `02-capability-diagnostic` | UIA, MSAA and Win32 see no rows; image and OCR do; the search box works through UIA | **PASS** | 11/11 |
| `03-correct-write-seed-21` | one approved payment, verified selection, confirmed postcondition, then no second write | **PASS** | 11/11 |
| `04-correct-write-seed-22` | same with another seed (row order, window position, column widths) | **PASS** | 11/11 |
| `05-correct-write-seed-23` | same with a third seed | **PASS** | 11/11 |
| `06-duplicate-rows` | two rows with identical Account, Type and Amount: Ref # decides, never the first match | **PASS** | 9/9 |
| `07-target-off-screen` | target below the visible rows: real wheel scrolling, then located again | **PASS** | 5/5 |
| `08-sort-between-locate-and-write` | the grid is re-sorted after prepare: execute locates again by business identity | **PASS** | 5/5 |
| `09-parasite-window` | a topmost window covers the grid: no input, operation back to NOT_STARTED | **PASS** | 4/4 |
| `10-decoy-window` | a window with the same title from another executable is rejected | **PASS** | 4/4 |
| `11-approval-checks` | EUR refused; the CLI issues no token for other values; missing, unknown, foreign, expired, previous-process and reused tokens refused | **PASS** | 18/18 |
| `12-concurrent-same-approval` | two servers execute with the same token at once: one write | **PASS** | 5/5 |
| `13-concurrent-two-operations` | two operations at once on one server: serialised, both correct | **PASS** | 5/5 |
| `14-freeze-after-post` | the app freezes after saving the payment: UNKNOWN, restart, new request refused, one payment | **PASS** | 10/10 |
| `15-freeze-before-post` | the app freezes before saving: UNKNOWN too (the cost), human reconciliation, then one payment | **PASS** | 8/8 |
| `16-driver-crash-after-submit` | the server dies between the submit input and its check: IN_FLIGHT becomes UNKNOWN at restart | **PASS** | 5/5 |
| `17-intent-journal-failure` | the operation database is read-only on disk: INTENT_NOT_PERSISTED, no input, approval not consumed | **PASS** | 5/5 |
| `18-ocr-lookalike-glyphs` | an amount drawn with letter O and letter l: refused rather than written | **PASS** | 3/3 |
| `19-ocr-dropped-group` | one reader reads 1,000.00 as 100.00 (injected): the readers disagree, refused | **PASS** | 3/3 |
| `20-screen-text-is-data` | a cell that says APPROVE PAYMENT grants nothing | **PASS** | 4/4 |
| `21-architecture` | driver and server never reference the application or the verifier | **PASS** | 7/7 |
| `23-guard-only-defense` | the payment was saved but the restarted app still shows the invoice Open: only UNKNOWN refuses it, whatever the account spelling | **PASS** | 11/11 |
| `24-second-server-mid-write` | a second server starts while the first is between its intent record and its confirmation: nothing is taken from the first | **PASS** | 7/7 |
| `26-slow-save-left-alone` | the save takes 8 s with the window answering: UNKNOWN, and the dialog is never cancelled after the Post click | **PASS** | 5/5 |
| `25-unexpected-exception` | a Win32Exception before, then after, the Post click: NOT_STARTED, then UNKNOWN that a human can reconcile; never left IN_FLIGHT | **PASS** | 8/8 |
| `27-full-coverage-500-rows` | 501 rows, look-alike rows near both ends, scan started from the bottom: every row is read before any click | **PASS** | 5/5 |
| `28-real-wrong-click` | a real click lands on the neighbouring row: the highlighted row is read, the route stops, no second click, no payment | **PASS** | 4/4 |
| `29-access-denied` | the application runs above the server's integrity level, and MSAA answers E_ACCESSDENIED: ACCESS_DENIED, no fallback, no input | **PASS** | 6/6 |
| `30-uia-failures` | an error inside a UIA call keeps its code; a UIA call past its time limit writes no further field | **PASS** | 7/7 |
| `31-pixel-check-strict` | 1,000.00 drawn, 1,200.00 claimed (and the reverse): refused; the same value: accepted | **PASS** | 5/5 |
