namespace Cntryl.Portia;

// Reported when a delivery's reservation ended while its request was dispatched: the transport owns the delivery
// again and will redeliver it, so the runner records the fault and leaves it alone.
sealed class QueueReservationLostException()
    : Exception("The queue reservation was lost while the request was dispatched; the transport will redeliver it.");
