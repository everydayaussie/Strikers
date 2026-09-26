# Changelog

## 1.1.0, 2026-09-26

Both players need 1.1.0. It cannot play against 1.0.0.

Fixed:

- A Dash machine that charges and then attacks no longer crashes the other player's game.
- A turn from the other player that would put two machines on one square, or use a machine that is not theirs, is stopped before your game plays it, instead of risking a crash.
- A match won by a Dash kill no longer stops.
- A machine that turns in place to attack, or moves and then turns, no longer stops the match.
- A turn that starts with a machine destroyed by its own attack no longer stops the match.
- The player names at the top of the screen appear within a second or two of the match loading, and stay after a visit to the Glossary.
- No white square sits on your board while the other player takes their turn.
- "Your opponent left" no longer flashes while the other player is still loading into the match.
- A game that closes during a match says so, instead of saying the other player left.
- The Play panel no longer stutters as it resizes, and Stop no longer flashes the old screen.
- A git clone builds the same as the ZIP download, and the build names the .NET SDK it needs (x64, 10.0.401 or later).
- Strikers no longer closes when another program is using the clipboard. It says so, and you can try again.
- A saved board the game cannot play is refused when you create the invite, instead of stopping the match later.
- Creating an invite on a network that cannot look up the tunnel says so, instead of waiting for ever.
- A second Strikers window that tries to start a match says another one is running, instead of closing.
- If Set up the match fails because your game is not on the challenge list, Strikers says so, and pressing Set up the match again works. The other player's screen suggests the same after a minute.
- The set-up screen no longer goes back to Choose your army when the other player's game takes a while to connect.
- If Strikers closes unexpectedly during a match on a small board, a later Machine Strike match can no longer crash the game.

New:

- Send report. After a stop, one zip holds both players' records of the match, with player names, army names, folder paths and the room code blanked out. Stop waits with a spinner while the zip is made. Send report opens a GitHub issue titled with what stopped the match, with the folder beside it.
- A report says what happened after the stop too, including a game that closed or crashed.
- The screen before a match shows your army's name where the game shows Beginner's Set.
- A stop brings Strikers to the front and flashes its taskbar button.
- The host sees who joined. New invite replaces an invite with a fresh one, and the old one stops working.
- An invite stops working after ten minutes with nobody in it, and shows the time it has left. The time pauses while your opponent is in.
- You can join without an army of your own.
- An army too big for the board says so, on both sides.
- Strikers looks for a newer version on GitHub as well as Nexus.
- After a Horizon Forbidden West update, Strikers starts no match until a Strikers version for the new game build is out, and says so.
- Join refuses an invite that points anywhere but the Strikers tunnel, and asks you to get a new one.
- Matching safety codes now also mean both PCs hold the same armies, board and rules. If they do not, Strikers stops and says the two setups do not match. If a message between the PCs goes missing, the match stops.
- The GitHub page shows how to check that the download is the real one.
- The Nexus page says what Strikers keeps in its folder, and the report page on GitHub says that anything dropped or written there is public.
- For modders, the GitHub page has a plain-words map of where Machine Strike keeps its information in the game's memory, and the same as short tables.

Changed:

- The safety code is four characters for each player, each in its own square. Yours stays hidden until you press the eye button, the code you type for the other player shows as dots, and Continue lights only when the codes match.
- Create lobby is now Create invite. In Settings, Random board and Random army are now Add built-in board and Add built-in army.
- The stop screen names what stopped the match.
- Every label, message and tooltip says what to do or what happened, once and plainly.
- Before this release every part of Strikers had a security review, and the fixes were tested in real matches on two PCs.

## 1.0.0, 2026-09-19

First release.
