using System;

namespace FinDeMes.Management
{
    /// <summary>Application command used by the Polleria domain to record one sale. Stable idempotency key.</summary>
    public sealed class RecordPolleriaSaleCommand
    {
        public string OrderId { get; }
        public int GroupId { get; }
        public long AmountMinor { get; }
        public int DayId { get; }

        public RecordPolleriaSaleCommand(string orderId, int groupId, long amountMinor, int dayId)
        {
            OrderId = orderId;
            GroupId = groupId;
            AmountMinor = amountMinor;
            DayId = dayId;
        }

        public string IdempotencyKey => ManagementIds.ReferencePolleriaSale + ":" + OrderId;
    }

    /// <summary>Application handler that routes Polleria sales into EconomyService and game events.</summary>
    public sealed class PolleriaPaymentHandler
    {
        readonly EconomyService economy;
        readonly GameEventHub hub;
        readonly EventJournal journal;
        readonly NotificationService notifications;

        public PolleriaPaymentHandler(EconomyService economy, GameEventHub hub, EventJournal journal, NotificationService notifications)
        {
            this.economy = economy ?? throw new ArgumentNullException(nameof(economy));
            this.hub = hub ?? throw new ArgumentNullException(nameof(hub));
            this.journal = journal ?? throw new ArgumentNullException(nameof(journal));
            this.notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        }

        public EconomyResult Handle(RecordPolleriaSaleCommand command)
        {
            if (command == null) return EconomyResult.Reject(EconomyStatus.RejectedAccounts, "null command");
            var request = TransactionRequest.ExternalCredit(
                TransactionKind.Sale,
                ManagementIds.AccountBusinessCash,
                command.AmountMinor,
                ManagementIds.ReferencePolleriaSale,
                command.OrderId,
                command.IdempotencyKey,
                "PolleriaSale");
            var result = economy.TryPost(request);
            if (!result.Accepted) return result;

            var envelope = new GameEventEnvelope(
                eventId: "evt:sale:" + command.OrderId,
                eventType: GameEventTypes.PolleriaSaleCompleted,
                priority: GameEventPriority.Info,
                gameMinute: result.Record.GameMinute,
                sourceReference: ManagementIds.ReferencePolleriaSale + ":" + command.OrderId,
                messageKey: "polleria.sale.completed",
                messageArgs: new[] { command.AmountMinor.ToString() },
                journal: true,
                popup: false,
                audio: false,
                autoPause: false);
            hub.Publish(envelope);
            journal.Append(envelope);
            notifications.Handle(envelope);
            return result;
        }
    }
}
