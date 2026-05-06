# Slay the Spire 2 - AI Communication Mod

This mod acts as a bridge between Slay the Spire 2 and an external AI program. It tracks the real-time combat state, sends the data as a JSON payload to a local server, and executes incoming commands (such as playing cards or ending the turn) from the AI.

## ⚠️ Prerequisites & Dependencies

To use this mod, you must have the following installed:
* **Slay the Spire 2**
* **BaseLib Mod**: This is a strict requirement. The mod will not function without BaseLib installed and enabled in your game.

## 🛠️ Development & Setup

* **IDE**: This project was developed using **JetBrains Rider**.
* **Template**: The project uses a ready-made community solution template designed for creating Slay the Spire 2 mods (such as custom cards, relics, and logic).

## 🚀 How It Works

1. **State Tracking**: The mod hooks into the game's `CombatStateTracker` and constantly monitors the player's health, hand, draw/discard piles, energy, and enemy intents.
2. **Data Export**: It packages this information into a JSON file and sends a POST request to a local server at `http://127.0.0.1:5000/update_state`.
3. **AI Execution**: If the server responds with a valid JSON command (e.g., `PlayCard` with specific target indices, or `EndTurn`), the mod forces the game to execute that action.

## 📥 Installation

1. Ensure you have the **BaseLib** mod installed in your Slay the Spire 2 mods directory.
2. Build the solution or download the compiled release.
3. Place the compiled mod folder into your Slay the Spire 2 `mods` directory.
4. Launch the game and ensure both BaseLib and this mod are enabled.
5. Make sure your local Python/AI server is running on port `5000` before entering combat.
