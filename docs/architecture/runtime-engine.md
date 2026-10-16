# Как работает runtime Fleans

[Открыть схему](runtime-engine.html)

Runtime — это цикл внутри Orleans grain `WorkflowInstance`. Один grain координирует один экземпляр процесса; `WorkflowExecution` содержит доменное состояние и правила его изменения.

1. **Вызов входит в grain.** `StartWorkflow` начинает выполнение. `CompleteActivity`, `FailActivity`, таймеры и сообщения продолжают существующий процесс. Обработчики результатов сначала применяют изменение к агрегату и вызывают `ResolveExternalCompletions`, затем запускают цикл.
2. **Цикл выбирает готовые шаги.** `GetPendingActivities` возвращает активные activity, у которых `IsExecuting == false`. Уже запущенная задача не запускается снова на каждой итерации.
3. **Выполняется логика BPMN-элемента.** `ExecuteAsync` работает через `ActivityExecutionContextAdapter`. Адаптер фиксирует намерения: отметить запуск, завершить шаг, опубликовать событие. Это не обязательно выполнение внешней бизнес-операции прямо в grain.
4. **Агрегат изменяет состояние.** `ProcessCommands` обрабатывает команды и возвращает инфраструктурные эффекты. Grain исполняет их через `EffectDispatcher`, публикует события адаптера и отмечает синхронно завершившиеся шаги.
5. **Вычисляются следующие шаги.** Runtime обрабатывает переходы, шлюзы, завершение областей и условные события. Если появились новые готовые activity, цикл повторяется. Если их нет — цикл прекращается.
6. **Сохраняются изменения.** В конце обычного вызова `ProcessPendingEvents` обрабатывает очередь внешних событий и вызывает `DrainAndRaiseEvents`: `RaiseEvent` → `ConfirmEvents` → запись в журнал → обновление CQRS-проекции. В длинном цикле есть промежуточная запись каждые 20 итераций; некоторые эффекты также сохраняют состояние перед внешним вызовом.
7. **Внешний результат снова запускает цикл.** Для `scriptTask`: `ExecuteScriptEvent` → Orleans Streams → `WorkflowExecuteScriptEventHandler` на Core → `ScriptExecutorGrain` на Worker → `CompleteActivity` или `FailActivity` исходного экземпляра. Для пользовательских задач и таймеров входы другие, но идея продолжения того же экземпляра сохраняется.

**Отсутствие готовых шагов не означает завершение процесса.** Он может ждать задачу человека, сообщение, таймер или результат вычисления. Ожидание не является бесконечным циклом опроса activity.

**Хранение и восстановление.** При активации grain читает snapshot и последующие события. Периодический snapshot записывается каждые 100 событий, также предусмотрен snapshot при корректной деактивации. Публикация stream-события и запись журнала не представлены как одна атомарная транзакция. Код отдельно отмечает ограничения детерминированности некоторых значений при replay; схема не обещает exactly-once выполнение внешних операций.

## Соответствие коду

- [Координатор, публичные входы и восстановление](../../src/Fleans/Fleans.Application/Grains/WorkflowInstance.cs)
- [RunExecutionLoop, ProcessPendingEvents и DrainAndRaiseEvents](../../src/Fleans/Fleans.Application/Grains/WorkflowInstance.Infrastructure.cs)
- [Доменный агрегат WorkflowExecution](../../src/Fleans/Fleans.Domain/Aggregates/WorkflowExecution.cs)
- [Логика ScriptTask](../../src/Fleans/Fleans.Domain/Activities/ScriptTask.cs)
- [Core-подписчик события скрипта](../../src/Fleans/Fleans.Application/Events/Handlers/WorkflowExecuteScriptEventHandler.cs)
- [Worker-исполнитель](../../src/Fleans/Fleans.Worker/Scripts/ScriptExecutorGrain.cs)
- [EF Core event store](../../src/Fleans/Fleans.Persistence/Events/EfCoreEventStore.cs)

Схема обобщает цикл, а не перечисляет все виды BPMN-эффектов. «Внешняя работа» иллюстрирует путь scriptTask; не каждый эффект проходит через Worker. Встроенные элементы просмотра HTML остаются на английском.
