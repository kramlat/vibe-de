public interface IPointerRouter : IDBusInterface
{
    Task RoutePointerEventAsync(PointerEvent evt);
}

public interface IKeyboardRouter : IDBusInterface
{
    Task RouteKeyEventAsync(KeyEvent evt);
}
