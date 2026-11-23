# Step 6 recovery and performance results

## Recovery

All five scenarios passed on an isolated Docker Compose stack with PostgreSQL and the payment simulator. The tests inspected both databases through process restarts. The crash trigger runs only in the RecoveryTest environment.

| Scenario | Observed result |
| --- | --- |
| Simulator approved, dropped the reply, then retail was killed before saving the result | Order `56b31976-7e19-4d0b-a220-231042c84f04` went from awaiting payment to confirmed after restart. Payment `c9a930a1-cd5b-4626-ae1c-155f6c75b65d` had one approval. Stock stayed at 1 on hand and 1 reserved. Replaying checkout returned the same order. |
| Retail killed immediately after saving confirmation | Restart and replay preserved one confirmation, one approval and one reservation. |
| Old worker resumed after a replacement confirmed the order | The replacement recovered the expired 30-second lease. The original worker logged that it ignored its stale claim. The order kept one confirmation and its original reservation. |
| Simulator offline for 60 seconds with 10 waiting orders | All 10 stayed pending with their units reserved. All 10 confirmed within 9.93 seconds of simulator readiness, against a 120-second target. |
| Simulator offline through eight attempts | The staff filter found the overdue order and exposed its last error. Customer resume was denied. Two simultaneous staff requests requeued the work once. The original payment confirmed and its one-unit hold stayed intact. |

The 15 PostgreSQL integration tests and three Chromium Playwright tests passed. An initial recovery run reused a checkpoint left by a killed process. The harness now clears that marker after restart. The recovery suite passed on a fresh stack, and the browser suite passed after clearing the test pause.

## Performance baseline

AMD Ryzen 3 5300U, Docker Desktop Linux engine 29.9.3, 8 logical CPUs and 3.51 GiB allocated. k6 1.4.2 ran inside Docker. Each run used one stocked SKU, one authenticated customer, distinct checkout keys and an approving simulator. The steady run overlapped isolated recovery and browser checks on the same machine. Source was a workspace snapshot without a Git commit. The tested retail image ID was `sha256:8c85a4e0f54daf20cede6f93e6b5a541c82c5d8efb01949f8b72637d8673cbe1`.

At 20 orders per second for five minutes, k6 sent 6,000 and retail admitted all 6,000. Submission p95 was 71.6 ms against a 500 ms target. Confirmation p95 was 5.809 seconds. At k6 run completion, four payments were pending, the oldest was 0.243 seconds old, and the last cleared after 0.350 seconds. All 6,000 approvals, active holds and reservation movements reconciled. Capacity rejections, unexpected errors and dropped iterations were zero.

The first burst saturated the load generator at 240 virtual users. It sent 2,660 checkouts and all 2,660 confirmed. A second pass with more virtual users produced one extra request at the end of the scheduling window. The script now caps submissions at the requested count.

The final 30-second burst sent and admitted exactly 3,000 orders at 100 per second. Submission p95 was 1.152 seconds and confirmation p95 was 44.159 seconds. At run completion, 1,995 payments were pending and the oldest was 20.310 seconds old. The backlog cleared in 45.576 seconds. Every order had one matching approval, active hold and reservation movement. There were zero capacity rejections, unexpected errors or dropped iterations.

## Reproduction

With the Playwright dependencies installed, run `./tests/recovery.ps1` for recovery. The original performance figures above came from the Step 6 snapshot. On the current build, `./tests/recovery.ps1 -Benchmark` runs the same two k6 workloads with the RabbitMQ timeline enabled. Each invocation creates its own stack and removes its test volumes on exit.

