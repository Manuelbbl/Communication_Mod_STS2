using Godot;
using System.Threading.Tasks;

namespace Communication_Mod.Communication_ModCode;

public static class VirtualController
{
    // You call this method when your Python AI sends a command!
    public static async void ExecuteCommand(string command)
    {
        string godotAction = "";

        // We map the simple AI commands to Godot's internal UI actions
        switch (command.ToUpper())
        {
            case "UP":     godotAction = "ui_up"; break;
            case "DOWN":   godotAction = "ui_down"; break;
            case "LEFT":   godotAction = "ui_left"; break;
            case "RIGHT":  godotAction = "ui_right"; break;
            case "ACCEPT": godotAction = "ui_accept"; break; // Confirm (A button / Enter)
            case "CANCEL": godotAction = "ui_cancel"; break; // Back (B button / Escape)
            default: return; // Unknown command is ignored
        }

        // 1. PRESS BUTTON
        var pressEvent = new InputEventAction();
        pressEvent.Action = godotAction;
        pressEvent.Pressed = true;
        Input.ParseInputEvent(pressEvent);

        // 2. WAIT BRIEFLY 
        // (Exactly like a real human. Without a delay, the game would 
        // register the press and release in the same frame and ignore it!)
        await Task.Delay(50); // Hold down for 50 milliseconds

        // 3. RELEASE BUTTON
        var releaseEvent = new InputEventAction();
        releaseEvent.Action = godotAction;
        releaseEvent.Pressed = false;
        Input.ParseInputEvent(releaseEvent);
        
        MegaCrit.Sts2.Core.Logging.Log.Info($"[VIRTUAL CONTROLLER] AI executed '{command}'.");
    }
    
}