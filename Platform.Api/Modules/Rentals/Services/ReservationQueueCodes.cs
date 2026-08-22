namespace Platform.Api.Modules.Rentals.Services;

public static class ReservationQueueCodes
{
    public const string Required = "QUEUE_REQUIRED";

    public const string Waiting = "QUEUE_WAITING";

    public const string TurnExpired = "QUEUE_TURN_EXPIRED";

    public const string TurnAlreadyUsed = "QUEUE_TURN_ALREADY_USED";

    public const string WaitingRoomClosed = "QUEUE_WAITING_ROOM_CLOSED";
}
