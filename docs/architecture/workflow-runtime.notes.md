# Runtime выполнения workflow
Сценарий: успешный scriptTask после создания и настройки WorkflowInstance.

## Цикл исполнения

- Показан успешный путь scriptTask после создания экземпляра.
- RunExecutionLoop собирает запрос скрипта; ProcessPendingEvents сохраняет накопленные изменения и затем отправляет скрипт в ScriptExecutorGrain односторонним вызовом (без stream).

## Ожидание и ошибки

- Таймер, сообщение или user task возобновляют тот же экземпляр.
- Ошибка скрипта → FailActivity; временную ошибку обратного вызова исполнитель повторяет, а потерянный результат экземпляр восстанавливает повторной отправкой (тайм-аут результата, активация).

## Восстановление и завершение

- Активация: snapshot + последующие события; периодический snapshot каждые 100 событий.
- После последнего шага фиксируется WorkflowCompleted; состояние читается через CQRS.

## Проверенные исходники

- `src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs`
- `src/Fleans/Fleans.Application/Grains/WorkflowInstance.Infrastructure.cs`
- `src/Fleans/Fleans.Worker/Scripts/ScriptExecutorGrain.cs`

Исполнитель — ScriptExecutorGrain (StatelessWorker + WorkerPlacement); экземпляр отправляет ему скрипт после ConfirmEvents. HTML-схема и `workflow-runtime.sequence.json` ещё показывают прежний путь через stream и WorkflowExecuteScriptEventHandler — их нужно перегенерировать.
