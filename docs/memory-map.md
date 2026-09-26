# Machine Strike memory map

This explains where Machine Strike, the board game inside *Horizon Forbidden West*, keeps its information while the game runs, and what happens when you change it. It is enough to read a match from another program, or to control one. Everything here was worked out by taking the Steam release apart and checking it against the running game.

[`memory-tables.md`](memory-tables.md) is the short version: tables of every value, code location and byte a working tool reads, writes or changes, and the IDs and stats of all 43 machines.

## Words used here

- **Address**: every byte of the game's memory has a number, like a house number. Numbers that start with `0x` are hexadecimal (base 16), so `0x10` is 16.
- **Byte and bit**: a byte holds one small number. It is made of 8 bits, on/off switches numbered 0 to 7.
- **Sizes**: `byte` and `int8` take 1 byte, `int` and `uint32` take 4 (an `int` or `int8` can be below zero), `float` is a 4-byte number with decimals, and `ptr` is an 8-byte pointer.
- **Offset**: how far into an object something is. `unit+0x3A` means "start at the piece and go 0x3A bytes in". That byte is the piece's health.
- **Pointer**: a spot in memory that holds another address, like a note saying "the board is over there". Square brackets mean "follow the note": `[inst+0x50]` is the address stored at `inst+0x50`, and it leads to the board.
- **RVA**: an address inside the game's program file, counted from where the program starts in memory. Add that starting point (the "base") to get the real address. A disassembler shows the same spot as `0x140000000` plus the RVA. RVAs change whenever the game is updated.
- **Vtable**: the first thing in every game object. It says what kind of object it is. Search memory for it and you find every object of that kind.
- **List**: the game stores a list (an "array") as three things: how many items it has, how much room it has, and where the items are. That takes 16 bytes: the count (4 bytes), the room (4 bytes), then the address of the items (8 bytes).
- **Function**: a piece of the game's code that does one job, like ending a match. Its first two inputs arrive in places called `rcx` and `rdx`.
- **Stored data (resource)**: data the game loads from its files, such as a machine's stats or a board's design. The live copies used during a match are built from it.
- **Flag**: a byte that means yes when it is 1 and no when it is 0.
- **Thread**: one line of work inside a program. A program can run several threads at once.
- **Patch**: a change to a few bytes of the game's own code while it runs. A **stub** is a small piece of new code that the patch jumps to.
- **Read**: one look at memory by a program that checks it over and over. "One read later" means the next look.

## How far to trust this

**Game version.** Horizon Forbidden West Complete Edition on Steam, build `667B1777-949F000` (two numbers from the program file's header, its date stamp and its size, in hex; the program has no version number), Steam build `14835813`. It has no Denuvo and no anti-cheat.

**After a game update.** Offsets inside objects come from how the game lays out its data, and they usually stay the same. RVAs, and the starting address at `base+0x8983150`, are positions in the program file and change with any update. The last section explains how to find them again.

**How sure.** A line with no tag was seen happening in the running game. `ASSUMED` marks something worked out from the game's code, or guessed from other facts, and never seen happening.

**What you need.** Reading needs nothing inside the game: the Windows functions `OpenProcess` and `ReadProcessMemory` reach everything here, and `WriteProcessMemory` changes it. Three things below also need your own code running inside the game: the patches for other board sizes, the hooks that record every action (with the fix for the computer's cursor), and asking the game's memory manager for memory.

## Finding a match in memory

```
g     = [base + 0x8983150]   the starting point; 707 of the game's functions read it
mgr   = [g + 0x190]          the challenge manager, which holds every challenge
c     = [mgr + 0x18]         the challenge being played (BoardGameChallengeInstance, 0xC8 bytes)
inst  = [c + 0xA0]           the match (BoardGameInstance)
logic = [inst + 0x50]        the board as it is being played (the "board logic")
```

The game follows this chain in one function (RVA `0xE3DF40`) that calls nothing else, so another program can follow it the same way. Every Machine Strike function the game's scripts can call uses it first, and stops if it gets zero: PauseGame (`0xE3E020`), IsUnitOnTile (`0xE3E080`), SetTutorialHighlight (`0xE3E100`) and AddRequiredTutorialMove (`0xE3E1D0`). The game also checks that the challenge is the right kind, against the type description at RVA `0x21769D0`. Skipping that check has never given a wrong result. A program that writes into the match can make the same check without calling the game: `c + 0x00` is the challenge's vtable, `0x190F750` for a board game challenge (the other kinds are under the challenge list below). ASSUMED for the challenge being played: that vtable was seen on every board game challenge in the list, and it belongs to the type the game checks for.

**The game itself treats a non-zero `logic` as "a match is loaded".** Loaded does not always mean being played:

- The match can be found only after the game has finished setting it up.
- Starting a challenge first builds a short-lived match object, which the real one then replaces.
- `logic` stays non-zero, and the board can still be read, on the victory and defeat screens until the player presses Continue.
- Retry keeps the same match object but rebuilds both player objects at new addresses. Quitting to the challenge list builds a new match object. So to spot a new match, watch the match address and `inst+0x58` together.

**The challenge list** belongs to the same manager:

```
mgr + 0x08     ptr     the entries, 0x20 bytes each: +0x10 the challenge, +0x18 an ID number (a hash)
mgr + 0x10     int     how many entries
mgr + 0x14     int     how much room
chal + 0x10    ptr     its stored data: +0x50 how many entry fees, +0x58 the fees (each: +0x28 the item, +0x30 how many)
chal + 0x28    ptr     its requirements: +0x20 how many, +0x28 the list (0x18 bytes each), +0x30 a condition
chal + 0x30    int     how many earlier challenges it depends on; writing 0 unlocks it
chal + 0x82    byte    unlocked flag
```

Each kind of challenge has its own vtable: `0x190F750` board game, `0x1911840` fighting pit, `0x190FCB8` combat arena, `0x19115E0` hunting ground. On the save these notes come from, none of the 63 board game challenges is locked; the menu only shows the ones for where the player is in the world.

## The match (BoardGameInstance)

```
inst + 0x00   ptr     the challenge's stored data (BoardGame)
inst + 0x28   byte    match over: 1 once the match has ended, however it ended
inst + 0x29   byte    pause: 1 here and 1 at +0x2A freezes the board; 0 and 0 lets it run
inst + 0x2A   byte    paused (the same meaning)
inst + 0x30   ptr     the winner; compare it with +0x40 and +0x48
inst + 0x40   ptr     player 0 (seat 0)
inst + 0x48   ptr     player 1 (seat 1)
inst + 0x50   ptr     the board logic
inst + 0x58   ptr     the player who moves first; set during setup and never changed
inst + 0x60   ptr     a spare stage slot: the game puts the next stage here; zero during play
inst + 0x68   ptr     the current stage of the match (placing machines, or playing)
inst + 0x78   int     machines destroyed so far, both sides added together (not the score)
```

**How a match ends.** The function at RVA `0xE3D200` ends a match. It does nothing if `+0x28` is already set. Otherwise it sets `+0x28` and `+0x30`, and shows the result message for as long as `settings+0x128` says (`GameWonPopupDisplayTime`). A program checking in a loop sees `+0x28` one read after the killing blow, when the destroyed machine leaves the list of pieces. When the last turn was replayed from scripted moves, the destroyed machine never leaves the list, but the flag is still set. Leaving a match from the pause menu counts as giving up: `+0x28` reads 1, `+0x30` names the other player, and both sides keep their machines.

**Pausing.** Writing 1 to both `+0x29` and `+0x2A` (what the game's own PauseGame writes) freezes the board: nothing can be picked, moved or previewed, the cursor stops following the squares, and hover highlights, the legend text and the hover card stop drawing. Writing 0 to both carries on exactly where play stopped. The freeze does not stop machines being placed. The two bytes belong to the match object, so a new match always starts unfrozen. The player's own pause menu does not use these bytes; it stops the board game some other way. Switching to another window does not stop it. ASSUMED from PauseGame's code: changing `+0x2A` calls the stage's vtable entry at `+0x10` when pausing and `+0x18` when carrying on.

### The stage

```
stage + 0x08   ptr     the match
stage + 0x10   ptr     the player whose turn it is
stage + 0x18   ptr     that player's controller (the object that runs their turn)
stage + 0x20   float   seconds of game time since this stage began
```

Each change of stage builds a new stage object, so if `+0x20` goes down, a new stage has started. `+0x20` goes up at the speed of real time, stops while the pause menu is open (the only clock found that does), and keeps going while the computer plays scripted moves.

The controller's vtable shows whose turn it is. It changes only when the turn passes to the other side.

While machines are being placed, `+0x68` holds the placing stage instead (`BoardGameInstanceUnitPlacing`, vtable `0x190E638`). Its `+0x10` is the player placing, and its `+0x18` is their placing controller, which is rebuilt for every machine placed. The game writes the next stage to `inst+0x60` before swapping it in, so check both slots and use the vtable to tell them apart. When both sides have placed everything, the placing stage replaces itself with the playing stage. A placing controller's vtable does not show which side it belongs to.

## The board

Two different things are both called "the board", and mixing them up is the easiest mistake to make:

- The **board logic** belongs to the match. It holds the squares of the match being played.
- The **stored board** belongs to the challenge. It is the board's design, and changing it before a match starts changes what loads.

### The board logic

```
logic + 0x08     ptr      the rows: +0x20 how many rows (the HEIGHT), +0x28 where the list of rows is
[[[logic+0x08]+0x28]]+0x20  int  the WIDTH: how many squares the first row has
logic + 0x18     int      how many squares
logic + 0x20     ptr      the squares, 0x48 bytes each, row by row: square (x, y) is number x + width*y
logic + 0x38     int      how many pieces
logic + 0x40     ptr      the pieces: one pointer per machine on the board; zero while the board is empty
logic + 0x48     object   a copy of the board from the start of the turn (below); +0x58 to +0x60 are inside it
logic + 0x58     int      the move limit: the smaller of the width and the height (ASSUMED from three boards)
logic + 0x5C     int      the end check's own copy of the points needed to win
logic + 0x60     int      0x7FFFFFFF, meaning "no turn limit"
logic + 0x68     int      turns taken by seat 0
logic + 0x6C     int      turns taken by seat 1

square + 0x20    ptr      how the square is drawn (see "Square highlights")
square + 0x40    int8     terrain
```

`logic+0x38` and `+0x40` have to be read one after the other, and when a machine is destroyed the list can close up in between. You then get the old count with the new list, and the last machine in two places. If the same pointer shows up twice, read again. The function at RVA `0xE3B5A0` finds the piece on a square by going through this list.

**Terrain.** From -2 to 3, the number is also what the square adds to a machine's attack power. The game's own names are not the ones players see.

| Value | Terrain | The game's name |
| --- | --- | --- |
| -3 | Blight | Blight |
| -2 | Chasm | Void |
| -1 | Marsh | Water |
| 0 | Grassland | Plains |
| 1 | Forest | Forest |
| 2 | Hills | Hills |
| 3 | Mountains | Mountains |

- A machine cannot be placed on Chasm. Chasm does not stop ground machines in general (a Scrounger and a Scorcher both ended moves on one), but a Grazer was not allowed onto one two squares ahead, and what decides that has not been found. Only a Dive machine can cross a Chasm in one move.
- Freeze turns its target's Marsh square into Grassland during the attack, one read before the damage shows.
- Writing `square+0x40` changes the rules straight away but does not redraw the square. Only the game's own terrain-change action redraws it.
- Every board that comes with the game looks the same when turned round 180 degrees. The game does not need that: a lopsided board loads and plays, and makes a better test, because a symmetrical one hides a wrong width or row spacing.
- The practice challenges' board always has the same terrain, the same 64 values in every match.

**When a turn passes.** Each time the turn passes to the other side, the game copies the board, swaps the copy into `logic+0x48`, and compares the two (RVA `0xE3F560`). For each square whose terrain changed, it lines up a terrain-change action (the square, packed into one byte, at `action+0x38`, and the new terrain at `+0x39`). For each piece whose health changed, it lines up a damage event, and those together become one blight-damage action. The other things that happen at that moment are running queued actions (`0xE3F060`), end-of-turn tidying (`0xE55290`) and building the next side's controller (`0xE554C0`). The tidying clears each piece's has-acted mark, adds the `unit+0x3B` counts to `unit+0x5C` and `+0x60`, adds one to the turn counters above, and adds the turn's length to `player+0x58`. All the action marks clear in one read at the end of a turn, and Spill damage follows one read later.

**Blight.** On a board that allows it (`+0x30` of the stored board), from the turn in `+0x34` onward, one square per player per turn turns into Blight (-3). The order is always the same: each player's own back row first, starting at their own back-right corner and switching sides each time, then the next row in. Each square is paired with the one opposite it (turned 180 degrees). A machine on Blight loses 2 health each tick, and ground machines can move onto it. Whether a tick is a turn or a length of time is not known (the changes came 4 to 15 seconds apart). No Red Blight square loads for the boards this save can reach, so a changed square is drawn as Chasm; Red Blight comes with two Leikttah boards. The code that picks which square changes has not been found.

**Square highlights.** `[square+0x20]` holds how the square is drawn: one byte for each drawing state, at `+0x30` plus the state's number, and a "needs redrawing" byte at `+0x3E`. State 9 (`+0x39`) is the white cursor square. State `0xB` is set by the controller as machines move on and off squares; leave it alone. State `0xC` (`+0x3C`) is the tutorial highlight. The game changes a state through RVA `0xE44260` and then tells the drawing code (RVA `0x9FC680`, on `state+0x28`). A change written from outside skips that step, so it only shows when the cursor passes over the square; writing `{1, 0, 1}` at `+0x3C` shows the tutorial highlight that way. Highlights only change what is drawn, never the game itself, and the "needs redrawing" byte can stay at 1 for minutes.

### Boards that are not 8x8

Without changes, the game only plays an 8x8 board correctly. There are two problems and one hard limit:

1. **One limit for both directions.** The move checks compare both x and y with `logic+0x58`, the board's smaller side. On a board wider than it is tall, the columns at `x >= height` cannot be moved into or attacked; on a board taller than it is wide, the rows at `y >= width` cannot. Machines still show there, and placing ignores the limit, so a machine can be put where it can never move. The check is in four places: the code that works out which squares a player may move to (RVA `0xE025A5`), two places whose result never reaches the screen (`0xE45F8E` and `0xE46106`), and the combat code's square stepper (`0xE04FCD`), which every attack type steps its target through, for the player and the computer alike. The list of machines a player can pick is built at `0xE3FC70`, which asks `0xE45A80` about each machine and is rebuilt only after an action; it leaves out any machine with nowhere to go. `0xE45A80` itself uses the real width.
2. **Rows read as if 8 squares long.** Before working out moves, the game builds a table with one terrain byte per square (RVA `0xE3B780`: a 200-byte space, the bytes from `+0x70`, the count at `+0xB0`) by copying the squares in order at the board's real width. Everything that reads that table (the move code, the combat code at `0xE02F00` with about a dozen places between `0xE03B00` and `0xE05200`, and the start-of-turn comparison) reads it as if every row were 8 squares long. So on any board that is not 8 wide, the game reads the wrong squares' terrain. On a 6x5 board a Shot did 5 and then 1 damage when the computer replayed it, where the shooter's own game did 3. On a 5-wide board, six of seven machines could not be picked, and a Chasm was offered as somewhere to move.
3. **8 is the most.** The move code's working space is 8 by 8 (its "already visited" map is 64 bytes), and a piece's square is stored as two 4-bit numbers that can be below zero.

The fix lets boards of any shape up to 8x8 play. Send the four checks to stubs that compare x with the width and y with the height, and change how the table is filled (RVA `0xE3B8B9`) so each row takes 8 places, with the unused ones set to zero. ASSUMED: nothing reads the unused places, since everything found that reads the table checks x against the width first. The bytes are in `memory-tables.md`.

The game's own fill has no limit: it writes one byte per square at the count and never checks it. A changed fill laid out for a narrower board than the one in play (a change left in the game by a program stopped before it put the code back) runs past the 64 places into the count itself. If that square's terrain is below zero (Marsh, Chasm, Blight), the count jumps past 250 and the squares after it land beyond the 200-byte space, which is on the stack. So the changed fill stores nothing once the count reaches 64 and holds it there: a leftover change then reads the wrong squares but writes nothing outside the table.

Warning: the move code (RVA `0xE023B0`, outside the range `0xE39000` to `0xE60000` that holds the rest of the board game's code) is used from five places, four of them the computer's, and a patch there that relies on what one of its inputs points to crashes the game at the end of a turn.

### The stored board, before a match

The stored board is lists inside lists, and **its size is simply the length of those lists**. There is no width or height value.

```
BoardGame + 0x08    16 bytes  the challenge's ID
BoardGame + 0x40    ptr       its settings (BoardGameSettings)
BoardGame + 0x48    list      its boards (BoardGameBoard)
board + 0x20        list      the rows; how many there are is the HEIGHT
board + 0x30        byte      blight allowed flag (EnableBlightTilePlacement)
board + 0x34        int       the turn blight starts on (2 on the boards that come with the game)
row + 0x20          list      the row's squares; how many there are is that row's WIDTH
each square         ptr       a shared square type: +0x28 its name, +0x50 its terrain (int8)
```

Pointing a square at a different square type changes that square's terrain. `settings+0xF8` lists every square type.

Warning: **`board+0x30` is ONE byte.** The three bytes after it are other data (two boards read `118DD7` and `940004` there), and writing 4 bytes there breaks them.

Changing the counts changes the size of board the game loads, within four limits:

1. **A bigger board needs real extra memory, not just a bigger number.** On the boards that come with the game, every list is exactly full, the rows' square lists sit 0x40 bytes apart, and row 0's squares come straight after the list of rows. So a row that claims to be longer reads into the next row, and a ninth row reads a square type as if it were a row.
2. **Rows of different lengths crash the game** when the match loads. The live board is made as row 0's width times the number of rows. To take squares away, use Chasm instead.
3. **Leaving the challenge list undoes the change**, and so does quitting a match, because the game rebuilds its stored data. Make the change, then start the match without going back.
4. **Keep the two placing areas apart**: `2 * depth <= height`. Each side's rows are counted from its own edge, so past that the middle rows belong to both sides.

Once, an 8x5 board gave the player no rows to place on, and it never happened again. In the same session a challenge could not be clicked until the menu was left and opened again, so it is taken to be a menu fault, not the board's shape.

### Settings

The match rules (`BoardGameSettings`, vtable `0x190E8A0`), at `BoardGame+0x40`:

```
+ 0x20    int     MaxVictoryPoints: the most points a side can score, AND the points that win
+ 0x24    int     how many machines a side can activate per turn; copied into the controller's +0x78
+ 0x28    int     BurstHealthCost: the health an Overcharge costs
+ 0x2C    int     MaxDraftPoints: 10 on every challenge
+ 0x30    int     MaxDuplicateDraftUnits
+ 0x50            CoinFlipDispayTime (spelled that way in the game)
+ 0x90    ptr     its +0x20 is the legend text (see "Text on screen")
+ 0xF8    list    every square type (AllTileTypes)
+ 0x108   list    every ability (AllAbilities)
+ 0x120   int     UnitPlacementRowCount: how many rows deep each placing area is
+ 0x128           GameWonPopupDisplayTime
```

MaxVictoryPoints read 2 on Beginner's Practice: Easy and 7 on Regular Challenge; ASSUMED: 6 on Medium. One settings object can be shared by several challenges (four were shared by seven), so a change affects the others too. A change does not survive quitting the match.

### The placing areas

`settings+0x120` is how many rows deep each placing area is: 2 on Beginner's Practice: Easy. It can be changed (3 gives three rows and lets a machine start a row further forward), and nothing else controls the areas. Seat 1 places on rows `0` to `depth-1`, and seat 0 on rows `height-depth` to `height-1`, worked out from the live height. Nothing moves an area away from its own edge.

Warning: **if a machine is placed outside its area, the game quietly moves it inside.** Check the square before writing it.

## Pieces

A piece (`unit` below) is a machine on the board during a match.

```
unit + 0x00   ptr      the player who owns it; compare with inst+0x40 and +0x48
unit + 0x08   ptr      the machine's stored data (BoardGameUnit): what it is and what it can do
unit + 0x38   byte     its square: x in bits 0-3, y in bits 4-7, each a 4-bit number that can be below zero
unit + 0x3A   byte     health
unit + 0x3B   byte     bits 0-1 facing, 2-3 actions this turn, 4-5 Overcharges this turn, 6 Overcharged
unit + 0x3D   byte     highlighted while it is being placed
unit + 0x3E   byte     set once it has been placed
unit + 0x5C   int      running total that each turn's action count is added to
unit + 0x60   int      running total that each turn's Overcharge count is added to
unit + 0x68   byte     bit 0: has acted this turn
unit + 0x70   2 x int  where it waits before it is placed
```

**Facing:** 0 north (towards low y), 1 east, 2 south, 3 west.

**The counts in `+0x3B`.** Each confirmed action adds one to the action count; a preview never does. An Overcharge adds to the Overcharge count and sets bit 6 when the extra action is confirmed, and does not add to the action count. The most seen is 2 actions and 1 Overcharge. All of one side's counts go back to 0 in one read at the end of its turn, and stay 0 through the other side's turn. The game writes them on every action, including turns replayed from scripted moves, and reads them at the end of the turn, so they are no place to keep anything of your own.

**`+0x68` bit 0** is never set by a preview or while placing, is cleared by an Overcharge (so a machine that acted twice reads as not acted, with bit 6 set), and clears at the end of the turn.

Timing, for a program that checks in a loop:

- Damage shows one read before the attacker's count goes up. An attack followed by a move counts once, when the move finishes, and the damage can show 10 or more reads before that.
- After a move is counted, the facing can still change, without another count, until the turn ends.
- On the action that ends the match, the count races the match's clean-up and is sometimes never seen.
- A machine destroyed by its own attack never shows its count.
- A destroyed machine stays in the list at 0 health for at least one read, and is often still there in the last read after a match-ending blow.

**Writing to a piece.** A health change is real straight away: the computer's next attack used the new value. Changing the facing bits turns the machine for the rules, but does not turn its model on screen.

Warnings:

- **Do not identify a machine by its place in the list.** The list closes up when a machine is destroyed. Machines start in army order, which makes this an easy mistake.
- **`unit+0x38` changes during previews.** Moving the cursor moves the piece (RVA `0xE48230` writes the square, moves the model through `0xE458F0`, and sends the messages `MsgEntityTeleported` and `MsgBoardGameTileSelectionChanged`), so a square read at the wrong moment is one nobody chose.
- **While placing**, a machine joins the list as soon as the cursor appears, on a fixed waiting square ((3,7) on the practice board), moves with the cursor, and gets its facing last.
- **`unit+0x70` is not the attack type.** The attack type is at `+0x70` of the machine's stored data, which `unit+0x08` points to.

**Effects outside an attack.** Spill: at the start of each turn, every piece within the machine's attack range loses 1 health, its own side's included (range 1 for Bristleback and Slaughterspine, 2 for Bellowback). Confuse: at the start of each turn, every piece in range turns to face the other way, without being marked as acted. Retaliate happens inside the attack that sets it off. Ram and Charge carry the attacker onto the square it hit as part of the attack, and neither a Ram nor a push sets a facing. ASSUMED: a Dive moves its attacker only when the square in front of the target is empty. The health an Overcharge costs can destroy the machine that used it.

## Machine and ability data

The stored data for one kind of machine (`BoardGameUnit`, 0x88 bytes, vtable `0x1911808`), which `unit+0x08` points to:

```
+ 0x08    16 bytes  ID; it names the KIND of machine, so two Burrowers share one
+ 0x20    ptr       name: a text object with the letters at +0x20 and the length (2 bytes) at +0x28
+ 0x48    ptr       its ability (BoardGameUnitAbility); zero if it has none
+ 0x50    int       health
+ 0x54    int       cost: how many army points it takes
+ 0x58    int       how far it moves
+ 0x5C    int       how far it attacks
+ 0x60    int       attack power
+ 0x64    int       HeightOffset
+ 0x70    byte      attack type: 0 Strike, 1 Shot, 2 Dash, 3 Ram, 4 Dive, 5 Tow
+ 0x80    4 x int8  armour: front at +0x80, back at +0x81, left at +0x82, right at +0x83
```

Combat compares whole-number attack with the armour on the side that was hit. There is no luck involved.

The ability data (`BoardGameUnitAbility`, vtable `0x190FBD0`), which `+0x48` points to:

```
+ 0x20    ptr       name
+ 0x38    byte      skill: 0 none, 1 Roam, 2 Stalk, 3 Scurry, 4 Climb, 5 Spread, 6 Shield, 7 Retaliate,
                    8 Burn, 9 Freeze, 10 Seed, 11 Unearth, 12 Blind, 13 Enpower, 14 Stun, 15 Spill, 16 Confuse
```

These are the names in the game's code, and several differ from what the game shows. The attack types Strike, Shot, Dive and Tow show as Melee, Gunner, Swoop and Pull. The skills Roam, Scurry, Climb, Spread, Seed, Unearth, Stun, Spill and Confuse show as Gallop, Climb, High Ground, Sweep, Growth, Alter Terrain, Drain, Spray and Whiplash, and Enpower, spelled that way in the code, shows as Empower. The tables give both names side by side.

**`+0x48` can be changed, and the game follows it everywhere.** Point it at another loaded ability from the challenge menu, then start the match without reloading the menu, and both the machine's card (name, icon, text) and the rules change. Tried with skill 14, which no machine has.

Addresses of machine data survive starting a match, but are lost when the challenge menu reloads; after quitting and starting again, only the IDs are the same. Ability data belongs to the settings, and one kept its address through a menu reload that moved all the machine data. The names `BoardGameBoardUnit` and `mHealth`, found in other sources, do not exist in this version.

## Players

The player objects at `inst+0x40` and `+0x48`, 0x80 bytes each:

```
player + 0x20   ptr     the side's player data (AIBoardGamePlayer on the computer's side)
player + 0x28   ptr     the side's army choice (BoardGameDraft)
player + 0x30   ptr     the army and starting squares in use (a DraftSetup)
player + 0x40   int     machines not yet placed: how many
player + 0x44   int     how much room
player + 0x48   ptr     the machines not yet placed (piece pointers)
player + 0x50   int     victory points
player + 0x58   list    how long each turn took (ASSUMED to be a list); its first number reads as the turn number
player + 0x68   int     scripted moves: how many
player + 0x6C   int     how much room
player + 0x70   ptr     the scripted moves, 0x18 bytes each
```

`+0x40` goes down when the player picks a machine to place, and back up if they cancel. It was once seen to change when a machine was destroyed during play, which has not been checked again. ASSUMED: `player+0x00` points 0x20 bytes into the object AddRequiredTutorialMove is given.

The two kinds of player data are `HumanBoardGamePlayer` (vtable `0x1911A80`) and `AIBoardGamePlayer` (`0x190E7A8`). Seat 0 was the player and seat 1 the computer in every match watched, but the game does not promise that.

**The computer's player data** (`AIBoardGamePlayer`, 0x80 bytes), at `player+0x20` on its side:

```
+ 0x48   float      chance of playing at Low difficulty
+ 0x4C   float      chance of Medium
+ 0x50   float      chance of High (the three add up to 1; Easy reads 1, 0, 0)
+ 0x54   2 x float  pause before activating a machine or Overcharging: shortest and longest, in seconds
+ 0x5C   2 x float  pause while thinking
+ 0x64   2 x float  pause before attacking
+ 0x6C   2 x float  pause before moving
+ 0x78   ptr        DisableAIMovesFact (see "Facts")
```

- **A very long thinking pause stops the computer** without stopping the game, and needs no code change.
- `+0x78` is zero on the Easy opponent, and pointing it at a fact set to true did not stop the computer.
- This data is thrown away and rebuilt every time a match loads, so a change has to be made again whenever the pointer changes. A late write to the thrown-away copy landed in the next match's list of listeners and crashed the game.

**How the computer decides.** A function at RVA `0xE56480` reads the three chances, uses the game's random numbers, and builds one of `LathiumPlayerEasy`, `LathiumPlayerMedium` or `LathiumPlayerHard` (vtables `0x1906AD0`, `0x19068D8`, `0x19073F8`). That object only exists while the computer is thinking, which it does on a separate thread, and the playing controller's `+0x350` points to it:

```
+ 0xF0    4 bytes   the decision: action, from square, to square, 0
+ 0xF4    byte      read along with the decision
+ 0xFC    byte      done flag
+ 0x100   int       job state
+ 0x108   handle    the Windows event it signals when done
its vtable: +0x10 reads the done flag, +0x18 checks the event, +0x20 starts the job, +0x28 hands back the decision
```

A similar function (RVA `0xE563C0`) builds the computer's placing controller.

## The playing controller

The controller is the object that runs one side's turn, at `stage+0x18` during play. The player's is a `HumanBoardGamePlayingControllerInstance` (vtable `0x190D7C0`) and the computer's an `AIBoardGamePlayingControllerInstance` (`0x190D708`). Both are built on `BoardGamePlayingControllerInstance` (`0x190DD38`) and have the same fields. A new one is made every turn, so look it up again each turn instead of keeping the address.

```
ctrl + 0x00    ptr       vtable: the only field that shows whose controller it is
ctrl + 0x30    list      actions
ctrl + 0x48    ptr       its +0x08 is the match
ctrl + 0x50    ptr       the player it plays for
ctrl + 0x78    int       machines left to activate this turn (from settings+0x24)
ctrl + 0x7C    byte      1 ends the computer's turn
ctrl + 0x80    2 x int   the selected square, x then y
ctrl + 0x88    ptr       the machine being used (also at +0x328)
ctrl + 0xA0    2 x int   the square the latest action started FROM
ctrl + 0xA8    2 x int   the square the latest action went TO; a free Rotate has TO the same as FROM
ctrl + 0xB0    byte      ASSUMED: the machine's facing when it was activated; never the facing an action chose
ctrl + 0xB8    int       squares it can reach: how many
ctrl + 0xC0    ptr       squares it can reach, which the attack checks the cursor against
ctrl + 0xE8    ptr       the movement plan (+0x00 how many steps, +0x08 the steps); changes on a move, and
                         is zero on the computer's controller until its first move
ctrl + 0x320   byte      1 while a machine is activated
ctrl + 0x348   byte      the computer's busy flag for scripted moves (see "Controlling the computer");
                         on the player's side, a pointer to a scripted move when the tutorial set one
ctrl + 0x350   ptr       the computer's thinking object, while it thinks
ctrl + 0x440   int       waiting actions: how many
ctrl + 0x448   ptr       waiting actions: the list
ctrl + 0x450   int       waiting actions: which one runs next; when it reaches the count, the computer
                         decides for itself (RVA 0xE39BD0)
```

What these fields hold:

- `+0x88`, `+0xA0` and `+0xA8` hold junk until the first machine is activated that turn.
- With a machine selected but not moved, `+0x80`, `+0xA0` and `+0xA8` all hold its square.
- They only remember the latest action: an attack writes FROM the same as TO, an attack made without moving leaves no trace once its owed move is made, and an attack overwrites the move's FROM.
- Previewing an owed move and then leaving it writes the cursor's square into TO, and cancelling clears `+0x320` with the cursor's square still in TO. So `+0x320` at 0 does not mean an action was confirmed.
- After a Dive, FROM holds the landing square and TO the square it attacked from.
- Nothing records which machine was hit, and a push does not change these fields.
- If the machine acting is destroyed as its action is confirmed, `+0x88` is cleared and FROM and TO are kept. Once the list closes up, `+0x88` can point to a machine that is no longer listed. Losses with no action behind them (Spill, the Overcharge cost) leave nothing here.

**The white square during the other side's turn** is the computer controller's `+0x80`, drawn as square state 9. It is lit when the selection changes, not checked every frame, so writing `(-1,-1)` there moves nothing. RVA `0xE40040` selects the square and lights it only while `+0x320` is 0; `0xE41790` sets state 9 through `0xE44260` and sends `MsgShowBoardGameContextualActions`.

**Vtable entries.** Each of the three vtables has four: `+0x00` deletes the object; `+0x08` deactivates (`0xE3F240` for the computer and the base, `0xE48DF0` for the player; both turn off the selected square's light); `+0x10` activates; `+0x18` runs on every update (`0xE39A30` computer, `0xE48BD0` player). The computer's and the base's activate (`0xE3F3B0`) refreshes the machine highlights, then lights the selected square if no machine is activated. A new controller's selected square is (0,0), so the computer's turn starts with a white square in that corner. The player's activate (`0xE48F70`) runs the base one, then selects the player's machine nearest the middle. Pointing the computer vtable's activate entry (RVA `0x190D718`) at a stub that calls the original activate and then `0xE41790` with 0 starts each computer turn with no square lit.

### How an action is confirmed

Both sides confirm actions through the same four functions. Each is given the controller in `rcx`. Each starts with five bytes (`mov [rsp+disp8], rbx`) that save a value and do not depend on where the code sits in memory, so a five-byte jump can take their place:

```
activate    RVA 0xE404F0   (controller, machine, how): sets +0x320
move        RVA 0xE40800   (controller, &slot): slot is two pointers, zero going in, filled with the action
attack      RVA 0xE40BD0   (controller, &slot)
Overcharge  RVA 0xE405D0   (controller, machine)
```

`how` is 0 Activate, 1 Rotate, 2 Burst (Overcharge), 3 Placement. The computer always gives 0, and the player's Rotate gives 1. The player's own code calls these from RVA `0xE49590` (activate and Overcharge), `0xE4A030` (move) and `0xE497D0` (attack, which first checks the square against `+0xC0`). The computer's move is at `0xE3ADA0` and its attack at `0xE3AF30`, and it turns machines with `0xE43100` and `0xE43560`.

What a turn looks like at these four functions:

- Every action is an activate naming the machine, then a move or an attack. FROM and TO are already set when move starts, and an attack's FROM and TO are the step before the strike.
- An Overcharge is an Overcharge call, then the extra action's own calls, and its health cost lands after that action is confirmed. The call happens even if the player cancels the Overcharge.
- A free Rotate is an activate and a move onto the machine's own square, with its facing already turned. A Rotate that turns an acted machine to face a target is an activate and an attack from its own square to its own square.
- An activate on its own does not use up an activation.
- A Dive or Dash attack gives its starting square as both FROM and TO, and the machine may be somewhere else by the next read. A Dash lands at the end of its range and needs that square to be empty.
- For a walk and then a charge, the move call names the walk.
- When a blow ends the match, the attacker's count never goes up and the move it owed has no call.
- The facing an action ends with is written after the function starts, so read it from the piece afterwards. A move's facing is final once the machine's count goes up where it lands. For an attack without moving after a turn, even the first read after the attack call can still show the old facing, so the facing the attack used is the attacker's facing on the read where the target first loses health.
- On an activate and an Overcharge, FROM and TO are left over from the last action (junk on a new controller), the selected square (`+0x80`) is the machine's own square, and the second input (`rdx`) is the machine. On a move and an attack, FROM and TO belong to the action and `+0x88` is the machine.
- End Turn is only offered for a machine that has acted.
- With the game's own controls, a move that ends facing a target attacks it automatically.

A tool can record every action by replacing each function's first five bytes with a jump to a stub that copies the controller's fields and jumps back. `memory-tables.md` has the layout of one that works. Copy the fields, but never follow them: the machine pointer is junk when activate starts, and following it crashed the game on the first click. The game refuses memory that is both writable and runnable as code, so the code and the saved records need separate blocks of memory.

## Controlling the computer: scripted moves

These are the moves the tutorial scripts. `AddRequiredTutorialMove(machine, action, x, y, p5, p6)` (RVA `0xE3E1D0`) adds a scripted move to the player's list (`player+0x68` how many, `+0x6C` room, `+0x70` the list), making the list bigger when it is full. It checks x and y against the board, and finds the player by matching its first input against the match's players. A tool can also write scripted moves straight into the list.

```
+ 0x00   byte    action: 0 activate, 1 Overcharge, 2 move, 3 attack, 4 end turn
+ 0x08   int     x
+ 0x0C   int     y
+ 0x10   byte    facing, compared with the machine's facing
+ 0x11   byte    not known
```

**How the computer runs them.** On each update (RVA `0xE39A30`) the computer hands out a scripted move only while its busy flag at `ctrl+0x348` is 0. The hand-out code (`0xE3A780`) sets the flag, takes the first scripted move, and sets up one follow-up call, so only one move runs at a time. Activate, Overcharge and end turn (`0xE4EAA0`, `0xE4EB20`, `0xE4EBE0`) take the move off the list and clear the flag first. Move and attack (`0xE4EBA0`, `0xE4EBC0`) only run while `ctrl+0x320` is set, and take the move off when the animation finishes. Scripted moves only run on that side's own turn. Activate first walks any machine that is still activated onto the move's square (using `0xE40040`), then looks for the machine there, so a machine that attacked without moving, and still owes its move, is moved there. After an Overcharge attack in place, or an Overcharge move, no machine stays activated, and a machine destroyed by its own attack or its Overcharge cost is off the machine list by the time the next scripted move has been taken off.

- **An attack's square is where the ATTACKER stands**, not the target. The game works out who is hit from the attacker's square and facing. A charge is sent as an attack from the machine's own square.
- **Two scripted moves crash the game**: an attack that puts the attacker on its landing square beyond the target, and a charge that would land off the board.
- **An activate on an empty square fails without any sign.** It is taken off, the next move or attack does nothing, the busy flag stays set and no more scripted moves run, and the computer then plays a move of its own choice. Nothing reports it, and `inst+0x28` is not set.
- **An activate finds its machine by the square alone, with no owner test**, so on a square that holds the other side's machine it activates that machine. Read from the code; what the game then does with it has not been watched.
- **While either pause byte (`inst+0x29`, `+0x2A`) is set, the computer's update stops right after its match-over test** (which, when the match is over, still frees the scripted-move list first): nothing is handed out, and a scripted move already handed out waits without its pause counting down. Read from the code, and watched twice with a move waiting: the move never ran, and taking the list away under the pause left the game running. Taking the scripted moves away while one is waiting is safe only while the pause holds, because the waiting move, when it runs, takes itself off a list that is no longer there.
- **An action of 5 or more cannot pause the computer's turn.** The hand-out code sets the busy flag and runs nothing, and the computer then plays its own move.
- **Never let the count reach 0 while the list sits in memory the game did not hand out.** At 0 the game gives the list's memory back to its memory manager (see "The game's memory manager"). End the list with extra end-turn moves.
- **After the blow that ends the match**, the turn is over and the next scripted move is refused. The check is `80 79 28 00` at RVA `0xE39A41` (compare the match-over byte with 0), followed by `74 52` at `0xE39A45` (jump if it is 0). When the match is over the jump is not taken, so the move is refused and the list's memory is given back. Changing the `74` to `EB` (always jump) stops both, and it is the only code change needed to control the computer.

## Placing machines

The placing controller, at `+0x18` of the placing stage:

```
ctrl + 0x10   ptr     the player it places for
ctrl + 0x20   int     0 before a machine is picked, 1 once its square is chosen, 2 once confirmed
ctrl + 0x28   ptr     the machine on the cursor; zero before the pick
ctrl + 0x30   float   step timer: while above 0 the controller only counts it down;
                      a huge number (1e9) holds the next step, and 0 lets it go
```

The army choice decides which controller a side gets. An army with set starting squares gets the automatic placer (vtable `0x190E2F8`); otherwise the player gets `0x190DCC0` and the computer gets `0x190E808`, which picks a random allowed square using the game's random numbers. The placing stage's update is at RVA `0xE556F0`, and it builds controllers at `0xE558C0`.

The automatic placer's update (RVA `0xE55C40`) goes through three steps:

1. **Pick** (`0xE47BC0`): puts the first machine in the player's not-yet-placed list at `+0x28`, sets that machine's `+0x3D` and clears the last one's, shows its stats card, and sets the timer to 0.5 seconds.
2. **Choose** (`0xE47CD0`): finds the machine at `+0x28` in the not-yet-placed list and takes it out (`0xE3E8B0`, which moves the rest to their waiting squares at `unit+0x70`), sets `unit+0x3E`, reads starting square number `army size - machines left`, adds the machine to the board's list, moves the cursor there, and sets the timer to 1.0 seconds.
3. **Confirm** (`0xE48530`): sends `MsgBoardGameUnitPlaced`.

- **Writing a different not-yet-placed machine to `+0x28` at state 0**, and moving its `+0x3D` byte with it, makes the choose step place THAT machine on the starting square. So machines can be placed in any order.
- A controller at state 0 has not added its machine yet. Which placement is under way: the side's piece count at state 0, and the count minus one from state 1 on.
- A change after the pick can only move the square (`unit+0x38`). The game's rules and labels follow it; the model does not.
- The starting square's direction byte becomes `unit+0x3B` bits 0-1.
- Placing passes to the other side only if that side still has machines to place, so with armies of different sizes the bigger one places the rest without handing over. While it is the other side's go, all of this side's machines are confirmed.
- The side that moves first also places first.
- The highlight message behind `+0x3D` cannot be sent from outside the game (`0xE457F0` writes `unit+0x3D` plus the state number, and sends `MsgBoardGameUnitHighlight`).

### Army choices

Each side of a challenge (`BoardGame`) has player data and an army choice (a "draft"):

```
BoardGame + 0x20 + seat*0x10   ptr     the side's player data
BoardGame + 0x28 + seat*0x10   ptr     the side's army choice (BoardGameDraft, vtable 0x190E400)

draft + 0x20    int     how many set armies (DraftSetups); 0, with a zero pointer, when the player builds their own
draft + 0x24    int     how much room
draft + 0x28    ptr     the set armies, 0x50 bytes each
draft + 0x30    byte    PlaceInstantly
draft + 0x31    byte    IsAllowedToStart (see "Who goes first")
draft + 0x32    byte    IsAllowedCustomDraft: 1 shows the screen where the player builds an army, 0 uses a set army

setup + 0x00    list    the army: pointers to machine data (BoardGameUnit), in army order
setup + 0x10    list    starting squares (BoardGameDraftPlacement), 0x10 bytes each: int x, int y, byte direction
```

On all thirteen set armies seen, the room equals the count, and a side without starting squares has room 0 and a zero pointer.

**A side where the player builds their own army can be given a set army instead**: write count 1, a pointer to a set army, and 0 at `+0x32`. The army screen is then skipped and the machines place normally. The two armies do not have to be the same size (6 against 7 and 3 against 5 both played). A side that already has room is better changed in place (the pointers and both counts), which only touches the game's own memory. Make the change before the match loads.

- **The game gives a set army's memory back to its memory manager** when the challenge list closes, so a set army in memory the game did not hand out crashes it on the way out. Point the side back at its own army after placing ends, not when the match starts: placing still reads the army after the machines appear, and switching back earlier crashes the game on the way in.
- **When Continue is pressed after a match, its armies are taken apart one entry at a time.** RVA `0xE56B30` calls `0xE56FE0` on the list at `+0x20` of the object (a count, then a pointer; 0x20 bytes per entry). Each entry's link at `+0x10` is let go through `0x1645DB0`, which reads through it first, and then the list's memory is given back through the memory manager's entry at `+0x98`. A pointer to memory of your own there crashes the game, and so do two armies sharing one block of memory.
- Retry keeps both sides' armies and starting squares at the same addresses. With an army in memory the game did not hand out, Retry crashed the game five times out of five.
- **Opening the challenge list again rebuilds the armies** and wipes any change. Make the change again after any trip through the menus.
- An army can lose its starting squares and keep its machines, so read both.
- An army the player builds is not in the set armies, even during a match. It is kept in the save: the DraftedUnits of a `BoardGameDraftPreset`.

**Saved armies.** `CollectionSave` (vtables `0x190E230` and `0x190E8C8`): `+0x28` the list of saved armies (DraftPresets), `+0x38` which one is selected. A saved army (`BoardGameDraftPreset`, vtable `0x190E848`): `+0x28` DraftedUnits, 0x20 bytes each, with the machine's ID at `+0x08` and how many of it at `+0x18`. The collection (`BoardGameUnitCollectionComponentResource`, vtable `0x190E5F8`): `+0x20` AllCollectableUnitTypes, `+0x30` MaxCountPerUnitType, `+0x34` MaxDraftPointCount, `+0x38` MinPointsRequired (each an `int`).

## Winning and the score

```
inst + 0x28    byte   1 once the match is over
inst + 0x30    ptr    the winner
player + 0x50  int    that side's victory points
```

Victory points are the total cost of the machines a side has destroyed, **capped at `MaxVictoryPoints`** (`[[[inst+0x00]+0x40]+0x20]`), which is also the number that wins. If it is lower than the cost of the most expensive machine on the board, one kill ends the match (a Slaughterspine costs 10). The cap reads the setting live, so a value written during a match caps the next kill. The end check does not: it uses its own copy at `logic+0x5C`, so lowering the winning number during a match to a score already reached did not end the match.

The end check (RVA `0xE05000`) adds up the cost of each side's destroyed machines against that copy, with losing every machine as a separate way to lose. It returns 1 when side B lost and 2 when side A lost, and never reads `player+0x50`. The code that handles a destroyed machine is at RVA `0xE46930`. A win on points leaves the loser with machines on the board.

- `inst+0x78`, `player+0x58` and `player+0x40` all change when a machine is destroyed, and none of them is the score.
- On the turn that ends the match, the action marks never clear, so there is no end-of-turn signal.
- Quitting to the desktop unloads the match about 10 seconds before the game closes.

## Who goes first

When a match is set up (RVA `0xE54C00`), the game counts the sides whose army choice has `IsAllowedToStart` (`draft+0x31`) set:

- **Two or more:** a coin flip. Bit 16 of the game's random number decides between player 0 and player 1. It picks between the two seats directly, not between the allowed sides.
- **Exactly one:** that side. ASSUMED: read from the code, never seen happen.
- **None:** player 0. The tutorial army choices read 0, so they never flip.

The result goes to `inst+0x58` and never changes during the match. The check is at RVA `0xE54D7F` (`cmp [rax+0x31],0`), the count test at `0xE54F09`, and the flip at `0xE54F77`. `settings+0x50` is how long the coin flip is shown.

The random numbers (RVA `0x234E3B0`) are shared by the whole game (541 uses in 178 functions). Each new number is the last one times `0x19660D`, plus `0x3C6EF35F`. They move on about once a frame, start from a seed set once when the game launches (RVA `0xD2FCA0`), and are never reset.

**Choosing who goes first.** Replace the 19 bytes at RVA `0xE54F95` with `41 B8 n 00 00 00` (which sets the answer to n), followed by 13 `90` bytes (which do nothing), and player n goes first. The change must be in place before the match is set up, and if left there it only affects the coin flip. Writing `inst+0x58` afterwards does not work: the decision has already been made and used. Retry flips the coin again with the same match object.

## Text on screen

Some text on screen can be changed in memory, and the game shows the new text without needing a redraw.

- **The corner names** (`ALOY`, `OPPONENT`) are blocks of text with a 16-byte header in front. The header is four 4-byte numbers: how many things use the block, an ID number made from the text (a hash), the length, and the room. The copies on screen read 1, `0xFFFFFFFF` (no ID set), 4 or 8, and `0x1F` or `0x2F`. The room is the block's size minus one, and the text can be the room minus 16 bytes long (15 or 31). A longer name written in place, with the length updated, shows straight away and survives the end of a turn.
- **Decoys.** Writing these changes nothing on screen: the copies in the game's shared text store (a shared copy reads `0x80000001` users, has a real ID, and has no spare room) and the counted `Aloy` and `Opponent` copies (a text pointer at `+0x00` and an 8-byte length at `+0x08`, in 16-byte slots).
- **The names only exist while a match is loaded.** A search from a menu finds nothing.
- **The names are rebuilt when the Glossary closes**: the old blocks are given back, and new `ALOY` and `OPPONENT` blocks appear at new addresses, so a written name lasts until then. The shared copy keeps its address. ASSUMED from timing: the old blocks go when the Glossary opens.
- **Each name is drawn by a text object** (vtable `0x17894C8`, at least 0x140 bytes): colour (red, green, blue and see-through, 4 floats) at `+0x00`, the text's measured width at `+0x10`, the line height (38.3) at `+0x14`, a pointer to the text at `+0x30`, and the font size (28.0) at `+0x50`.
- **The turn banner's text belongs to a `LocalizedTextResource`** (vtable `0x188AE20`, 0x38 bytes, in a list with one every 0x40 bytes): ID at `+0x08`, how many use it at `+0x18`, flags at `+0x1C`, the text at `+0x20`, the length (8 bytes) at `+0x28`, a pointer at `+0x30`. The text is a bare 16-byte block, 15 letters and the end marker, with no header and no spare room.
- Warning: **pointing a name or the banner at memory of your own crashes the game**, even with a copied header. Only writes inside the game's own blocks are safe.
- **The right-hand name is lined up by its left edge**, and runs off a 16:9 screen after about 14 letters. Where it sits is not in the text object, the drawing buffers, or any pair of numbers found in memory; the menu layout works it out (`MenuTextResource` HorizontalAlign `+0x1D8`; `MenuVisualResource` X `+0x60`, Width `+0x6C`, AutoWidth `+0x70`, Align `+0x114`, margins `+0x144`; alignment 1 left, 2 centre, 3 right). The 16 live `TextWidget` objects (vtable `0x1846A68`) hold none of the names.
- **The army name on the screen before a match** ("Beginner's Set") is the same kind of block: header `{1, 0xFFFFFFFF, 14, 0x3F}`, room for 47 bytes, drawn by a text object (text pointer at `+0x30`, colour at `+0x00`, font size 21.0 at `+0x50`). It exists from the first time the screen loads, survives going back to the challenge list, and is given back when a match starts. It is COPIED from a source block when the screen is built (1 user at the list, 3 once built, room 15, given back at every match start), so a name written into the source first is on the screen the moment it appears. The screen copy's room follows the source's length (`0x3F` for 14 or 15 bytes, `0x2F` for 11). Only a text object points to the screen copy, which is how to tell it from the source. Text in UTF-8 (letters from any language) shows, and a long name is cut off at the button's edge. The `VariableLocatorInstance` just before the block (vtable `0x18DED58`; its `+0x38` is a `VariableLocatorResource`, vtable `0x18DD510`) is a neighbour, not its owner.
- **The legend text** (`PreviewUnitLegendText`) is at `[[settings+0x90]+0x20]`. It is UTF-8 ending in a zero byte, with no length stored, so new text must be no longer than the old. The 8 bytes after it are not checked. "Open Glossary" is built once, when the match screen loads.
- **What redraws.** A change only shows where the game reads the text again after something the player does. The hover card reads its values again. The board badge keeps its own copy and redraws on game events. The score in the corners only redraws on `MsgUpdateBoardGameScore`. During the other side's turn, the card panel stays on the computer's last machine.
- Every text search here is for the English words.

**Searching memory.** The names are in memory that only the game uses and that can be read and written (`MEM_PRIVATE`, `PAGE_READWRITE`), in small blocks. Of about 9.5 GB the game has in use, 8.8 GB is that kind, 576 MB is the game's program and its libraries, and 40 MB is mapped files. About 5 GB of the readable memory is graphics memory marked write-combined (`PAGE_READWRITE | PAGE_WRITECOMBINE`), which is slow for the processor to read. The speed limit is `ReadProcessMemory`, not the comparing: one thread took 40 to 57 seconds over all of it, eight took 8 seconds, and speed grows almost in step with threads up to about 12. Reading only the game's own read-write memory with eight threads takes 0.6 seconds.

## The game's memory manager

The memory manager (the "allocator") hands out memory and takes it back.

```
[ThreadLocalStoragePointer + 0x1A10]   each thread's own memory manager; if it reads zero, the game sets it to the main one
[base + 0x897A388]                     the main memory manager
manager's vtable + 0x88                hand out memory: rcx the manager, rdx the size
manager's vtable + 0x98                take it back
base + 0x1FE0510 and + 0x1FE0518       the start and end of a reserved area
base + 0x1FE1F00                       the memory manager that owns that area
```

The reserved area read `0x26DC0000000` to `0x271C0000000`, which is 16 GB. The game's own objects are inside it. Memory handed out by `+0x88` comes from outside it: above it for 0x1B0-byte blocks (`0x272C80424C0`, `0x272EA402840`, `0x272A8431A40`, `0x27402873F00`), and below it for 0x20-byte ones (`0x26A54730400` and two next to it). The memory is not cleared: it still holds what the last user left there. A working call also gives `r8d = 0x10` and `r9d = 0` (a third and fourth input); ASSUMED that the function uses nothing past the size.

When the game gives back a list's memory, it checks the address against the reserved area. Inside, it goes back to the area's owner; outside, to the thread's or the main memory manager. So memory from `+0x88` goes back to the manager that handed it out. **Memory from anywhere else, such as a block from `VirtualAllocEx`, goes to a manager that never handed it out, and crashes the game.** Only memory from `+0x88` is safe to leave where the game will give it back. Calling `+0x88` means running code inside the game, for example on a thread started with `CreateRemoteThread`.

These come from the functions at RVA `0xE56FE0` (which gives back an army's list) and `0xE58EA0` (which asks for 0x30 bytes). All five addresses move with a game update, and so do the start and end of the area.

## Other game systems

**Facts.** The game keeps true-or-false "facts" in one database object at RVA `0x897A600`. SetBooleanFact is at `0x348840` (it calls `0x346080`) and GetBooleanFact at `0x3488E0` (it calls `0x345DF0`); both are set up at `0x334BD0`. A fact's `+0x22` is its can-be-set flag, and `+0x28` holds the value that applies everywhere, which a plain lookup reads directly.

**Stored data.** Every piece of stored data starts with the same header:

```
+ 0x00   ptr        vtable, pointing into the program
+ 0x08   16 bytes   ID (GGUUID)
+ 0x18   uint32     its number within its group
+ 0x1C   uint32     the group's ID (the group:number address that game-file tools use)
```

The ID sits in memory as plain bytes, so loaded data can be found by searching for it. To turn the written form of an ID into memory order, reverse the bytes of each of the first three groups between dashes, and keep the rest as they are (`d2ee4d1d-d380-...` is stored `1D 4D EE D2 80 D3 ...`). The object is the search hit whose 8 bytes just before the ID point into the program. The game's type information (RTTI, the names of its kinds of object) can be read in this release version.

**Perks** (outside Machine Strike):

- `PerkCategory`: `+0x20` Visible (uint32); `+0x28` and `+0x2C` how many root perks there are and how much room, `+0x30` pointers to them; `+0x40` the name text. ASSUMED: `+0x38` a trophy and `+0x48` an icon, both zero in the two objects seen.
- `PerkLevel`: `+0x20` RequiredLevel, `+0x24` Tier, `+0x28` Cost (uint32); `+0x78` name, `+0x80` description and `+0x88` icon pointers, which change what the skills menu shows when pointed elsewhere. ASSUMED: `+0x90` a preview video, and one of the effect lists at `+0x50` to `+0x58`.

These were found by comparing a hidden and a visible category whose fields were known to differ. Check them the same way after a game update.

## Not found yet

- Anything that moves a placing area away from its own edge.
- Where the right-hand name's position is kept.
- The code that picks which square Blight takes.
- What stops some ground machines from entering a Chasm.

## After a game update

The RVAs and the starting address will move; the offsets inside objects probably will not. To find everything again:

1. **Find the function that finds the match.** Every Machine Strike script function calls it first and stops when it returns zero. It reads one fixed address and follows four pointers with no calls in between, which makes it easy to spot.
2. **Test the chain on a live match.** Follow it with a match running, then from the main menu. `[inst+0x50]` must be non-zero the first time and zero the second. That one test proves the whole chain.
3. **Check the board before anything else.** Read the width and height, then every square's terrain: each must be between -3 and 3. A wrong offset or row spacing shows up straight away as a number outside that range.
4. **Use a lopsided board.** A symmetrical one reads the same either way round and hides mistakes.
5. **Check a piece against the screen.** Unpack `unit+0x38` for a machine you can see and confirm its square, then compare `unit+0x3A` with its health.
6. **Check the game clock** before trusting any wait timed by the game: during a turn `stage+0x20` goes up with real time, and it stops while the pause menu is open.

Until the first five pass, nothing else here can be trusted. Before each code change, compare the original bytes in `memory-tables.md` with the game; if they differ, the code has moved. The changed table fill also relies on the instruction at its return point being the count's increment, so check that too.

**Write nothing on a build these notes were not checked on.** Read the build (the two header numbers under Game version at the top) before any write and refuse on any other: a byte that only looks like the original can sit at an old RVA on a new build, and a write there changes the new build's code. Once the steps above pass on a new build, add its two numbers to the builds you write on.

## Credit

Worked out from a bought copy of the game. Nothing here is copied from another project's code.
