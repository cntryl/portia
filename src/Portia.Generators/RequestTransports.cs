namespace Cntryl.Portia;

[Flags]
enum RequestTransports
{
    None = 0,
    Callable = 1,
    Queuable = 2,
    Notifiable = 4,
    Schedulable = 8
}
