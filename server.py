import json
from flask import Flask, request, jsonify

app = Flask(__name__)

@app.route('/update_state', methods=['POST'])
def receive_game_state():
    game_state = request.json
    
    # 1. OPTION: Save split up and neatly organized for you (Debugging)
    if "CardCatalog" in game_state:
        with open('debug_catalog.json', 'w', encoding='utf-8') as f:
            json.dump(game_state["CardCatalog"], f, indent=4, ensure_ascii=False)
            
    if "Piles" in game_state:
        with open('debug_piles.json', 'w', encoding='utf-8') as f:
            json.dump(game_state["Piles"], f, indent=4, ensure_ascii=False)
            
    with open('debug_player_enemies.json', 'w', encoding='utf-8') as f:
        # We save a filtered version here without the piles/catalog
        filtered = {k: v for k, v in game_state.items() if k not in ["CardCatalog", "Piles"]}
        json.dump(filtered, f, indent=4, ensure_ascii=False)


    # 2. OPTION: The "Pro-Way" for AI training (Appending history)
    # Appends the complete state as a compact line to the log file
    with open('ai_training_log.jsonl', 'a', encoding='utf-8') as f:
        f.write(json.dumps(game_state, ensure_ascii=False) + '\n')


    # --- Detailed console output based on the trigger ---
    trigger = game_state.get("Trigger", "Unknown")
    
    print(f"\n--- Update received: {trigger} ---")
    
    # COMBAT EVENTS
    if trigger == "OnTurnStarted":
        turn_number = game_state.get("TurnNumber", 0)
        hp = game_state.get("Player", {}).get("CurrentHp", "?")
        print(f"[COMBAT] Turn {turn_number} started. Player HP: {hp}")
    elif trigger == "AfterCombatRoomLoaded":
        print("[COMBAT] A new combat room was entered!")
        
    # NON-COMBAT EVENTS
    elif trigger == "Campfire":
        print("[CAMPFIRE] The AI has arrived at a campfire.")
    elif trigger == "SHOP_INITIAL":
        print("[SHOP] Shop entered. Reading offerings...")
    elif trigger == "SHOP_UPDATE":
        print("[SHOP] AI bought something, inventory updated.")
    elif trigger == "Event":
        event_id = game_state.get("EventId", "?")
        print(f"[EVENT] Question mark room entered. Event ID: {event_id}")
    elif trigger == "Treasure_Initial" or trigger == "Treasure_Update":
        print("[TREASURE] There is a chest in the room!")
    elif trigger == "MapGenerated":
        act = game_state.get("Act", "?")
        print(f"[MAP] World map for Act {act} generated. Targets can be calculated.")
    elif trigger == "Focus":
        node = game_state.get("NodeName", "Unknown")
        print(f"[UI FOCUS] Focus is currently on: {node}")
    elif trigger == "PlayerInventory":
        gold = game_state.get("Gold", 0)
        print(f"[INVENTORY] Player deck and relics loaded. Gold: {gold}")
        
    # LOOT EVENT (NEW!)
    elif trigger == "Loot":
        rewards = game_state.get("Rewards", [])
        print(f"[REWARD] Reward screen open! ({len(rewards)} available options)")
        for r in rewards:
            typ = r.get("Type", "Unknown")
            if typ == "Gold":
                print(f"  -> {r.get('Amount', 0)} Gold")
            elif typ == "Potion":
                print(f"  -> Potion: {r.get('PotionId', '')}")
            elif typ == "Relic":
                print(f"  -> Relic: {r.get('RelicId', '')}")
            elif typ == "Card":
                card_options = r.get("Options", [])
                print(f"  -> Card Choice (Can choose from {len(card_options)} cards)")
            else:
                print(f"  -> {typ}")
                
    else:
        # Fallback
        print(f"[INFO] Miscellaneous event detected.")
    
    return jsonify({"status": "OK"}), 200

if __name__ == '__main__':
    print("Starting AI Server...")
    print("Waiting for data from Slay the Spire 2 on port 5000...")
    app.run(host='127.0.0.1', port=5000)