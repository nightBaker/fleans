# Runtime выполнения workflow
Сценарий: успешный scriptTask после создания и настройки WorkflowInstance.

## Цикл исполнения

- Показан успешный путь scriptTask после создания экземпляра.
- RunExecutionLoop публикует событие; ProcessPendingEvents затем сохраняет накопленные изменения.

## Ожидание и ошибки

- Таймер, сообщение или user task возобновляют тот же экземпляр.
- Ошибка скрипта → FailActivity; ошибка этого вызова передаётся stream provider для повтора.

## Восстановление и завершение

- Активация: snapshot + последующие события; периодический snapshot каждые 100 событий.
- После последнего шага фиксируется WorkflowCompleted; состояние читается через CQRS.

## Проверенные исходники

- `src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs`
- `src/Fleans/Fleans.Application/Grains/WorkflowInstance.Infrastructure.cs`
- `src/Fleans/Fleans.Application/Events/Handlers/WorkflowExecuteScriptEventHandler.cs`
- `src/Fleans/Fleans.Worker/Scripts/ScriptExecutorGrain.cs`

Подписчик — WorkflowExecuteScriptEventHandler (CorePlacement); исполнитель — ScriptExecutorGrain (WorkerPlacement). Публикация и ConfirmEvents не показаны как одна транзакция. Доставка подписчику асинхронна; её позиция на схеме не гарантирует порядок относительно записи журнала.
