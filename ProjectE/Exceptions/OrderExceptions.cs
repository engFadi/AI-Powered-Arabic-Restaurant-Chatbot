namespace ProjectE.Exceptions 
{ 
    public class MenuItemNotFoundException : Exception
    {
        public MenuItemNotFoundException(string message) : base(message) { }
    }

    public class MenuItemUnavailableException : Exception
    {
        public MenuItemUnavailableException(string message) : base(message) { }
    }

    public class InvalidQuantityException : Exception
    {
        public InvalidQuantityException(string message) : base(message) { }
    }

    public class DuplicateMenuItemException : Exception
    {
        public DuplicateMenuItemException(string message) : base(message){ }
    }
    public class EmptyOrderItemsException : Exception
    {
        public EmptyOrderItemsException(string message) : base(message) { }
    }

}
