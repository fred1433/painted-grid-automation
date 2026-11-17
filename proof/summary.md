| Scenario | What it checks | Result | Checks |
|---|---|---|---|
| `01-protocol-surface` | tools/list, annotations, structured errors, stdout carries JSON-RPC only | **PASS** | 10/10 |
| `02-capability-diagnostic` | UIA, MSAA and Win32 see no rows; image and OCR do; the search box works through UIA | **PASS** | 11/11 |
| `03-correct-write-seed-21` | one approved payment, verified selection, confirmed postcondition, then no second write | **PASS** | 11/11 |
| `04-correct-write-seed-22` | same with another seed (row order, window position, column widths) | **PASS** | 11/11 |
| `05-correct-write-seed-23` | same with a third seed | **PASS** | 11/11 |
| `06-duplicate-rows` | two rows with identical Account, Type and Amount: Ref # decides, never the first match | **PASS** | 7/7 |
| `07-target-off-screen` | target below the visible rows: real wheel scrolling, then located again | **PASS** | 5/5 |
| `08-sort-between-locate-and-write` | the grid is re-sorted after prepare: execute locates again by business identity | **PASS** | 5/5 |
| `09-parasite-window` | a topmost window covers the grid: no input, operation back to NOT_STARTED | **PASS** | 4/4 |
| `10-decoy-window` | a window with the same title from another executable is rejected | **PASS** | 4/4 |
| `11-approval-checks` | missing, foreign, other row, other amount, other process, expired and reused tokens | **PASS** | 13/13 |
| `12-concurrent-same-approval` | two servers execute with the same token at once: one write | **PASS** | 4/4 |
| `13-concurrent-two-operations` | two operations at once on one server: serialised, both correct | **PASS** | 5/5 |
| `14-freeze-after-post` | the app freezes after saving the payment: UNKNOWN, restart, new request refused, one payment | **PASS** | 10/10 |
| `15-freeze-before-post` | the app freezes before saving: UNKNOWN too (the cost), human reconciliation, then one payment | **PASS** | 8/8 |
| `16-driver-crash-after-submit` | the server dies between the submit input and its check: IN_FLIGHT becomes UNKNOWN at restart | **PASS** | 5/5 |
| `17-intent-journal-failure` | the intent cannot be recorded: no input, approval not consumed | **PASS** | 4/4 |
| `18-ocr-lookalike-glyphs` | an amount drawn with letter O and letter l: refused rather than written | **PASS** | 3/3 |
| `19-ocr-dropped-group` | one reader reads 1,000.00 as 100.00 (injected): the readers disagree, refused | **PASS** | 3/3 |
| `20-screen-text-is-data` | a cell that says APPROVE PAYMENT grants nothing | **PASS** | 4/4 |
| `21-architecture` | driver and server never reference the application or the verifier | **PASS** | 7/7 |
