## Spectating yourself on a second PC

There are two ways to do this. **Simple Spectate Mode** works over the internet and follows you
into private matches and lobbies; **Spectate Me** is the older local-network method.

---

## Option 1 — Simple Spectate Mode (recommended)

Follows you using Friends presence, so the two PCs don't need to be on the same network and it
works for public matches, private matches and social lobbies alike.

### You will need

- Echo VR installed on both PCs
- Spark installed on both PCs
- API enabled in the game settings on both PCs (Spark can turn this on for you)
- Both copies of Spark logged into Discord with **the same account**

### Setup

1. On your playing PC:
   1. Open Spark and log in with Discord (button at the top left)
   2. Launch the game and play as normal

2. On your spectating PC:
   1. Open Spark and log in with Discord — the same account
   2. Go to **Settings → Simple Spectate Mode**
   3. Choose **Myself, on my other PC**
   4. Click **Launch Spectator**

The spectator client launches and then follows your playing PC into every session it joins,
switching automatically whenever you change match or drop back to a lobby.

While it's following, the spectating copy of Spark stops publishing its own presence — both PCs
sign in as the same Discord account and share one presence row, so otherwise the spectator would
overwrite the session it's trying to read and end up chasing itself.

### Spectating anyone else

Pick **Anyone, by name** instead and type their Echo VR or Discord name.

Anyone can be followed into public matches — Spark searches the public match API by name, no setup
needed on their end. If they're also on your Friends list and running Spark, you'll follow them into
private matches and lobbies as well, because their own client publishes the session id and those
sessions are never listed publicly.

---

## Option 2 — Spectate Me (same network only)

### You will need

- Both PCs capable of running Echo VR on the same network
- Spark installed on both PCs
- API enabled in the game settings on both PCs

### Setup

1. On your playing PC (the one with your headset):
   1. Launch the game normally and join a private/public match or lobby
   2. Launch Spark
   3. The IP Address in Spark should be set to 127.0.0.1 (or click the Local PC button)

2. On your spectating PC:
   1. Open Spark
   2. Click "Settings"
   3. Put your playing PC's local IP address in the area for Quest IP.
   4. Change the port to 6724
   5. If the playing PC is in a match/lobby and has Spark running, then Spark should say "Connected" at this point.
   6. Click "Spectate Me"

If all is well, then your spectator will follow you into pubs and privates.
