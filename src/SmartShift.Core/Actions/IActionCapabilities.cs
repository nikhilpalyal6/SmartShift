namespace SmartShift.Core.Actions;

public interface IActionCapabilities
{
    bool IsAvailable(string actionId);
}
