# eero Desktop — hands-on checklist

The automated tests never change your network, so the actions below have only been checked against a fake eero
server. Try them in this order: safest first. After each step, check the result in the eero phone app or another
eero Desktop page, then put it back (the **Undo Last Change** button in the header works for most of them).

If anything fails, note the message in the status bar (bottom of the window) and tell Claude which step it was.

## 1. No effect on anyone (try these first)

| # | Do this | You should see | Put it back |
|---|---|---|---|
| 1 | Devices › select a device you own › **Rename** › add " test" to the name | Status "Device renamed."; the new name in the list and in the eero app | **Undo Last Change** (header) |
| 2 | Devices › tick **Needs a Name** › select one › **Rename** (accept or edit the suggested name) | The device leaves the "Needs a Name" list | Undo, or keep the name |
| 3 | Profiles › select a profile › **Rename** › add " test" | New name in the list and the eero app | Undo |
| 4 | Advanced › **Power saving** switch (no restart) | Status "Power saving is now on/off" | Undo |
| 5 | Reservations & Ports › Port Forwards › select **JDownloader** › **Enable / Disable** twice | Enabled column flips to No, then back to Yes | (second click restores it) |

## 2. Brief interruption for one device or profile

| # | Do this | You should see | Put it back |
|---|---|---|---|
| 6 | Devices › select your phone › **Pause / Resume** | Phone loses internet within seconds; Status shows Paused (yellow light) | Undo (or Pause / Resume again) |
| 7 | Profiles › select **Guest** › **Pause / Resume** | Devices in that profile lose internet | Undo |
| 8 | Devices › select a spare device › **Assign Profile** › pick a profile | Profile column changes | Undo |
| 9 | Reservations & Ports › IPv6 Pinholes › **Add** › your NAS, port **32401**, TCP | A second pinhole row, labeled with the NAS name | Select it › **Delete** |

## 3. Brief interruption for everyone (do when nobody minds)

| # | Do this | You should see | Put it back |
|---|---|---|---|
| 10 | Advanced › **UPnP** switch (may briefly restart eeros) | Confirmation first, then "UPnP is now off/on" | Undo |
| 11 | Advanced › **Block Ads with Control D** (every eero restarts a few minutes later) | Ad Blocking shows "Ads are being blocked with Control D" | Undo switches back to the previous DNS |
| 12 | Wi-Fi & Guest › guest network name › add " 2" › **Save** | Guest SSID changes; guests reconnect | Undo |

## Not on this list on purpose

- **Wi-Fi password and main network name**: every device has to reconnect. Undo can rename the network back but
  never stores old passwords.
- **Restart Network / restart an eero, Block a device, Delete anything**: work the same way as the steps above but
  are disruptive or need re-creating by hand.

## Afterwards

Open **Security Check** and **History › Devices** to see the changes reflected, and tell Claude which steps passed.
