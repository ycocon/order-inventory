# Step 7 RabbitMQ delivery results

The automated test ran against RabbitMQ 4.4.1 and PostgreSQL in a disposable Docker Compose stack. It checked both the staff order API and the persisted event rows.

| Scenario | Observed result |
| --- | --- |
| Publisher killed after RabbitMQ confirmed the publish and before PostgreSQL marked the outbox sent | The first copy was already visible in the timeline while the outbox remained unsent. Restarting the publisher sent event `0c779940-84c2-42e8-a609-b4980fd9eef0` again. The reader logged the duplicate, the outbox marked it sent and the timeline kept one row. |
| Reader killed after saving the timeline row and before acknowledgement | RabbitMQ redelivered event `d6b29ab4-d6b5-45a5-9de2-4a5ac2a23131` with its redelivery flag set. The restarted reader acknowledged it and the timeline kept one row. |
| Events arrived in version order 3, 2, 1 | The latest timeline entry stayed **Order dispatched** while the older events arrived. The final timeline showed three entries in version order and retained the dispatch actor. |
| Invalid event arrived before a valid payment event | The invalid message reached the dead-letter queue, and the valid payment event appeared in the timeline. |
| RabbitMQ stopped with both workers running | A new checkout confirmed its payment with one unit reserved during the outage. Both events waited in the outbox. After RabbitMQ restarted, both became visible and the unsent count returned to zero. |

The existing demo’s 29 historical order events were backfilled into the timeline. An initial broker health check created an Erlang cookie owned by root before the broker could start. The check now probes the AMQP listener without touching the cookie. The demo’s cookie ownership was corrected and the suite passed again on a fresh broker volume.

All 15 PostgreSQL integration tests, three Playwright tests and three existing payment scenarios also passed. The checkout-to-dispatch browser test verified that staff saw one dispatch timeline entry.

Run `./tests/timeline.ps1` from the project directory with the Playwright npm dependencies installed. The command starts and removes its own Docker stack.

