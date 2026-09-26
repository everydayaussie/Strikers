# Machine Strike memory tables

| About | |
| --- | --- |
| game | Horizon Forbidden West Complete Edition, Steam |
| version | build `667B1777-949F000` (two numbers from the program file's header, its date stamp and its size), Steam build `14835813` |
| where it comes from | every entry is read, written or changed by working code while the game runs |
| after a game update | RVAs and the starting address move; offsets inside objects usually stay the same; check the build before any write and refuse on another |
| how sure | an entry with no tag was seen working; `ASSUMED` means worked out from the game's code and never seen |

## Words used here

| Word | Meaning |
| --- | --- |
| address | every byte of the game's memory has a number, like a house number; numbers starting `0x` are hexadecimal (base 16), so `0x10` is 16 |
| byte, bit | a byte holds one small number and is made of 8 bits, on/off switches numbered 0 to 7 |
| sizes | `byte` and `int8` take 1 byte, `int32` and `uint32` take 4 (the `int` ones can be below zero), `float` is a 4-byte number with decimals, `ptr` is an 8-byte pointer |
| offset | how far into an object something is: `+0x3A` means 0x3A bytes in from the start |
| pointer | a spot in memory that holds another address; `[x]` means "the address stored at `x`" |
| base | where the game's program, `HorizonForbiddenWest.exe`, starts in memory |
| RVA | an address inside the program file; base plus RVA is the real address, and a disassembler shows it as `0x140000000` plus the RVA |
| vtable | the first thing in every game object; it says what kind of object it is, and its RVA names that kind |
| list | how the game stores a list (an "array"): 16 bytes, the count (4 bytes), the room (4 bytes), then the address of the items; a row that says otherwise is only the address |
| function | a piece of the game's code that does one job; its first two inputs arrive in `rcx` and `rdx` |
| stored data | data the game loads from its files (a "resource"), such as a machine's stats |
| flag | a byte that means yes when it is 1 and no when it is 0 |
| patch | a change to a few bytes of the game's code while it runs |
| stub | a small piece of new code, in memory a tool asks for inside the game, that a patch jumps to |
| `90`, `E9` | in code, `90` is a byte that does nothing and `E9` starts a jump |

## Finding a match

| Step | Read | What you get |
| --- | --- | --- |
| 1 | `[base + 0x8983150]` | the game's starting point |
| 2 | `[start + 0x190]` | the challenge manager |
| 3 | `[manager + 0x18]` | the challenge being played (`BoardGameChallengeInstance`, 0xC8 bytes); its vtable at `+0x00` is `0x190F750`, a board game challenge (ASSUMED here, seen on the list) |
| 4 | `[challenge + 0xA0]` | the match (`BoardGameInstance`) |
| 5 | `[match + 0x50]` | the board as it is being played (the board logic); not zero while a match is loaded |

## Code changes

| RVA | What the code does | Original bytes | Written instead |
| --- | --- | --- | --- |
| `0xE39A45` | refuses scripted moves once the match is over, and gives their list's memory back | `74` (jump if 0, after `80 79 28 00` at `0xE39A41`) | `EB` (always jump) |
| `0xE54F95` | the coin flip that picks who moves first | `41 81 E0 01 00 00 80 7D 0A 41 FF C8 41 83 C8 FE 41 FF C0` | `41 B8 n 00 00 00` then 13 `90`: player n goes first |
| `0xE45F8E` | move limit check 1 (its result never reaches the screen) | `41 8B 49 58 0F BE D0 8B C2 C1 F8 04 3B C1 0F 8D CA 02 00 00` | `E9 <jump distance>` then 15 `90` |
| `0xE46106` | move limit check 2 (its result never reaches the screen) | `41 8B 49 58 49 8D 79 58 0F BE C0 C1 F8 04 3B C1 7D 2D 0F BE C2 C1 F8 04 3B C1 7D 23` | `E9 <jump distance>` then 23 `90` |
| `0xE025A5` | move limit check 3: works out which squares a player may move to | `44 8B 46 10 0F BE D0 8B C2 C1 F8 04 41 3B C0 0F 8D 4D 01 00 00` | `E9 <jump distance>` then 16 `90` |
| `0xE04FCD` | move limit check 7: the combat code's square stepper | `8B 53 10 0F BE C0 C1 F8 04 3B C2 7D 11 8B C1 C1 F8 04 3B C2 7D 08` | `E9 <jump distance>` then 17 `90` |
| `0xE3B8B9` | place 8: copies each square's terrain into the table the move and combat code read | `48 63 8A B0 00 00 00 88 44 11 70` | `E9 <jump distance>` then 6 `90` |
| `0xE404F0` | confirms an activate | `48 89 5C 24 08` | `E9 <jump distance>` |
| `0xE40800` | confirms a move | `48 89 5C 24 10` | `E9 <jump distance>` |
| `0xE40BD0` | confirms an attack | `48 89 5C 24 10` | `E9 <jump distance>` |
| `0xE405D0` | confirms an Overcharge | `48 89 5C 24 08` | `E9 <jump distance>` |
| `0x190D718` | the computer's controller: its activate entry | a pointer to `0xE3F3B0` | a pointer to the cursor stub |

| Place | Stub goes back to | Stub turns down to | Stub does |
| --- | --- | --- | --- |
| 1 | `0xE45FA2` | `0xE4626C` | checks x against the width and y against the height |
| 2 | `0xE46122` | `0xE46145` | checks x against the width and y against the height |
| 3 | `0xE025BA` | `0xE02707` | checks x against the width and y against the height; it reaches the board as `rsi = logic + 0x48` |
| 7 | `0xE04FE3` | `0xE04FEB` | checks x against the width and y against the height |
| 8 | `0xE3B8C4` | none | runs the first instruction it replaced, which reads the count at `+0xB0`. At 64 or more, or below zero, it stores nothing and sets the count to 63, which the game's own increment at the return point makes 64. Otherwise it runs the store, then after each row's last square writes `8 - width` zero bytes and adds `8 - width` to the count at `+0xB0`, so every row takes 8 places |

| Old place, no longer changed (places 4 to 6, which earlier versions of Strikers changed) | Original bytes | Ends at | Note |
| --- | --- | --- | --- |
| `0xE02465` | `42 8D 0C C2 48 63 D1` | `0xE0246C` | put back to the original if found changed |
| `0xE024E8` | `8D 04 C8 48 98` | `0xE024ED` | put back to the original if found changed |
| `0xE025D1` | `8D 04 CA 48 63 F0` | `0xE025D7` | put back to the original if found changed |

## Stub memory

| Move limit block | Offset | Holds |
| --- | --- | --- |
| width | `+0x00` | `int32`, from the live board |
| height | `+0x04` | `int32`, from the live board |
| stubs | `+0x10` | places 1, 2, 3, 7 and 8 in that order, each starting 0x10 bytes after the end of the one before |
| how they read | | places 1, 2, 3 and 7 read the width and height from this block; place 8 has the width built in |

| Action code block (runnable, not writable) | Offset | Holds |
| --- | --- | --- |
| marker | `+0x000` | `0x474E49524D4F4353` |
| records block | `+0x008` | the address of the records block |
| activate stub | `+0x010` | |
| move stub | `+0x110` | |
| attack stub | `+0x210` | |
| Overcharge stub | `+0x310` | |
| cursor stub | `+0x410` | `53 48 83 EC 20 48 8B D9 48 B8 <0xE3F3B0> FF D0 48 8B CB 33 D2 48 B8 <0xE41790> FF D0 48 83 C4 20 5B C3`: runs the original activate, then turns off the selected square's light |
| how to find it | | follow the jump at each of the four confirm functions; each must land on its own stub, all in one block; the block starts 0x10 before the first stub |

| Records block (readable and writable) | Offset | Holds |
| --- | --- | --- |
| count | `+0x000` | `uint32`, how many records have been written |
| records | `+0x040` | 32 records of 0x40 bytes; the next is written to record number `count` mod 32 |

| One record | Offset | Holds |
| --- | --- | --- |
| kind | `+0x00` | `uint32`: 1 activate, 2 move, 3 attack, 4 Overcharge |
| number | `+0x04` | `uint32`: the count once this record is added |
| controller | `+0x08` | the controller (`rcx` when the function starts) |
| machine | `+0x10` | the controller's `+0x88`: the machine on a move or attack, junk otherwise |
| from | `+0x18` | x at `+0x18` and y at `+0x1C`, copied from the controller's `+0xA0` and `+0xA4`: only means something on a move or attack |
| to | `+0x20` | x at `+0x20` and y at `+0x24`, copied from the controller's `+0xA8` and `+0xAC`: only means something on a move or attack |
| second input | `+0x28` | `rdx`: the machine on an activate or Overcharge, a space for the result on a move or attack; never followed |
| vtable | `+0x30` | `[controller]`: which side confirmed the action |
| selected | `+0x38` | x at `+0x38` and y at `+0x3C`, copied from the controller's `+0x80` and `+0x84`: the machine's own square on an activate or Overcharge |
| writing order | | the stub writes every other field first, then the kind, then the number, then the count; a reader takes a record only when its number is one more than the last one it took |
| going back | | the stub runs the five bytes it replaced and jumps back; it follows no pointer except the controller |

| Memory request block | Offset | Holds |
| --- | --- | --- |
| result | `+0x00` | the address the memory manager handed out |
| done | `+0x08` | set to 1 when the call finishes |
| size | `+0x10` | how much memory was asked for |
| the call | | `hand_out(manager, size, 0x10, 0)` through the manager's vtable `+0x88`, on a thread started inside the game |

## Vtables

| RVA | Kind of object |
| --- | --- |
| `0x1911808` | `BoardGameUnit`: one kind of machine's stored data |
| `0x190E780` | `BoardGame`: a challenge's stored data |
| `0x190E8A0` | `BoardGameSettings`: a challenge's rules |
| `0x190FC28` | `BoardGameTile`: a square type, which a stored row's squares point to |
| `0x190FBD0` | `BoardGameUnitAbility`: an ability |
| `0x190E3D8` | `BoardGameBoard`: a stored board |
| `0x190E468` | a row of a stored board |
| `0x190E848` | `BoardGameDraftPreset`: a saved army |
| `0x190E400` | `BoardGameDraft`: a side's army choice |
| `0x190E5F8` | `BoardGameUnitCollectionComponentResource`: the machine collection |
| `0x190E230` | `CollectionSave`: the saved collection |
| `0x190E8C8` | `CollectionSave`, its second vtable |
| `0x190E7A8` | `AIBoardGamePlayer`: the computer's player data |
| `0x1911A80` | `HumanBoardGamePlayer`: the player's player data |
| `0x190E560` | `BoardGameInstanceCoinFlip`: the coin flip stage |
| `0x190E638` | `BoardGameInstanceUnitPlacing`: the placing stage |
| `0x190E2F8` | `BoardGameAutoUnitPlacingControllerInstance`: places a set army automatically |
| `0x190E808` | `AIBoardGameUnitPlacingControllerInstance`: the computer placing its own army |
| `0x190D7C0` | `HumanBoardGamePlayingControllerInstance`: runs the player's turn |
| `0x190D708` | `AIBoardGamePlayingControllerInstance`: runs the computer's turn |
| `0x190DD38` | `BoardGamePlayingControllerInstance`: what both of those are built on |
| `0x1906AD0` | `LathiumPlayerEasy`: the computer thinking, at Easy |
| `0x190F750` | a board game challenge |
| `0x1911840` | a fighting pit challenge |
| `0x190FCB8` | a combat arena challenge |
| `0x19115E0` | a hunting ground challenge |

## Fields

| Object | Offset | Size | What it holds |
| --- | --- | --- | --- |
| challenge manager | `+0x08` | ptr | its entries, 0x20 bytes each |
| challenge manager | `+0x10` | int32 | how many entries |
| challenge manager | `+0x14` | int32 | how much room |
| manager entry | `+0x10` | ptr | the challenge |
| manager entry | `+0x18` | uint32 | an ID number (a hash) |
| challenge | `+0x08` | ptr | the object the game's type check looks at |
| challenge | `+0x10` | ptr | its stored data |
| challenge | `+0x28` | ptr | its requirements |
| challenge | `+0x30` | int32 | how many earlier challenges it depends on; 0 unlocks it |
| challenge | `+0x82` | byte | unlocked flag |
| requirements | `+0x20` | int32 | how many |
| requirements | `+0x28` | ptr | the list, 0x18 bytes each: a pointer, an `int32` at `+0x08`, a pointer at `+0x10` |
| requirements | `+0x30` | ptr | a condition |
| challenge's stored data | `+0x50` | int32 | how many entry fees |
| challenge's stored data | `+0x58` | ptr | the fees, one pointer each |
| fee | `+0x28` | ptr | the item |
| fee | `+0x30` | int32 | how many |
| match | `+0x00` | ptr | the challenge's stored data (`BoardGame`) |
| match | `+0x28` | byte | match over flag |
| match | `+0x29` | byte | pause: 1 here and at `+0x2A` freezes the board; 0 in both lets it run |
| match | `+0x2A` | byte | paused: either byte at 1 counts as paused |
| match | `+0x30` | ptr | the winner |
| match | `+0x40` | ptr | player 0 |
| match | `+0x48` | ptr | player 1 |
| match | `+0x50` | ptr | the board logic |
| match | `+0x58` | ptr | the player who moves first |
| match | `+0x60` | ptr | a spare stage slot: holds the next stage while machines are placed; zero during play |
| match | `+0x68` | ptr | the current stage |
| stage | `+0x08` | ptr | the match |
| stage | `+0x10` | ptr | the player whose turn it is |
| stage | `+0x18` | ptr | that player's controller |
| stage | `+0x20` | float | seconds of game time in this stage; the pause menu stops it |
| board logic | `+0x08` | ptr | the rows |
| rows | `+0x20` | int32 | the height |
| rows | `+0x28` | ptr | where the rows are; `[[rows + 0x28]] + 0x20` is the width |
| board logic | `+0x20` | ptr | the squares, 0x48 bytes each; square (x, y) is number `x + width*y` |
| board logic | `+0x38` | int32 | how many pieces |
| board logic | `+0x40` | ptr | the pieces, one pointer each |
| board logic | `+0x58` | int32 | the move limit that both x and y are checked against |
| square | `+0x20` | ptr | how the square is drawn |
| square | `+0x40` | int8 | terrain |
| how a square is drawn | `+0x3C` | byte[3] | tutorial highlight, the next state, then "needs redrawing": written as `{1 or 0, 0, 1}` |
| challenge's stored data (`BoardGame`) | `+0x08` | 16 bytes | the challenge's ID |
| `BoardGame` | `+0x20 + seat*0x10` | ptr | that side's player data |
| `BoardGame` | `+0x28 + seat*0x10` | ptr | that side's army choice (`BoardGameDraft`) |
| `BoardGame` | `+0x40` | ptr | the rules (`BoardGameSettings`) |
| `BoardGame` | `+0x48` | list | its stored boards |
| stored board | `+0x20` | list | the rows; how many there are is the height |
| stored board | `+0x30` | byte | blight allowed flag; the next three bytes are other data |
| stored board | `+0x34` | int32 | the turn blight starts on |
| stored row | `+0x20` | list | the squares, each a pointer to a square type; how many there are is the width |
| square type | `+0x28` | ptr | its name |
| square type | `+0x50` | int8 | its terrain |
| rules | `+0x20` | int32 | `MaxVictoryPoints`: the most points a side can score, and the points that win |
| rules | `+0x28` | int32 | `BurstHealthCost`: the health an Overcharge costs |
| rules | `+0x2C` | int32 | `MaxDraftPoints` |
| rules | `+0x30` | int32 | `MaxDuplicateDraftUnits` |
| rules | `+0xF8` | list | every square type |
| rules | `+0x108` | list | every ability |
| rules | `+0x120` | int32 | `UnitPlacementRowCount`: how many rows deep each placing area is |
| rules | `+0x100` to `+0x13F` | 64 bytes | read whole for inspection, in four pieces at `+0x100`, `+0x110`, `+0x120` and `+0x130`; only `+0x120` is known |
| piece | `+0x00` | ptr | the player who owns it |
| piece | `+0x08` | ptr | its machine's stored data (`BoardGameUnit`) |
| piece | `+0x38` | byte | its square: x in bits 0-3, y in bits 4-7, each a 4-bit number that can be below zero |
| piece | `+0x3A` | byte | health |
| piece | `+0x3B` | byte | bits 0-1 facing, 2-3 actions this turn, 4-5 Overcharges this turn, 6 Overcharged |
| piece | `+0x3D` | byte | highlighted while being placed |
| piece | `+0x68` | byte | bit 0: has acted this turn |
| machine's stored data | `+0x08` | 16 bytes | the ID of this kind of machine |
| machine's stored data | `+0x20` | ptr | its name |
| machine's stored data | `+0x48` | ptr | its ability |
| machine's stored data | `+0x50` | int32 | health |
| machine's stored data | `+0x54` | int32 | cost |
| machine's stored data | `+0x58` | int32 | how far it moves |
| machine's stored data | `+0x5C` | int32 | how far it attacks |
| machine's stored data | `+0x60` | int32 | attack power |
| machine's stored data | `+0x70` | byte | attack type |
| machine's stored data | `+0x80` | int8[4] | armour: front at `+0x80`, back at `+0x81`, left at `+0x82`, right at `+0x83` |
| ability | `+0x20` | ptr | its name |
| ability | `+0x38` | byte | which skill |
| name text | `+0x20` | ptr | the letters |
| name text | `+0x28` | uint16 | the length |
| player | `+0x00` | ptr | ASSUMED: 0x20 bytes into the object AddRequiredTutorialMove is given |
| player | `+0x20` | ptr | that side's player data |
| player | `+0x28` | ptr | that side's army choice (`BoardGameDraft`) |
| player | `+0x30` | ptr | the army in use (`DraftSetup`) |
| player | `+0x40` | int32 | machines not yet placed: how many |
| player | `+0x48` | ptr | machines not yet placed |
| player | `+0x50` | int32 | victory points |
| player | `+0x68` | int32 | scripted moves: how many |
| player | `+0x6C` | int32 | scripted moves: how much room |
| player | `+0x70` | ptr | scripted moves, 0x18 bytes each |
| computer's player data | `+0x48` | float | chance of playing at Low difficulty |
| computer's player data | `+0x4C` | float | chance of Medium; the three chances add up to 1, which is how to tell the computer's side |
| computer's player data | `+0x50` | float | chance of High |
| computer's player data | `+0x54` | float[2] | pause before activating a machine: shortest and longest |
| computer's player data | `+0x5C` | float[2] | pause while thinking; a very long one stops the computer |
| computer's player data | `+0x64` | float[2] | pause before attacking |
| computer's player data | `+0x6C` | float[2] | pause before moving |
| computer's player data | `+0x78` | ptr | `DisableAIMovesFact`; setting it did not stop the computer |
| army choice | `+0x20` | int32 | how many set armies (`DraftSetups`) |
| army choice | `+0x24` | int32 | how much room |
| army choice | `+0x28` | ptr | the set armies, 0x50 bytes each |
| army choice | `+0x30` | byte | `PlaceInstantly` |
| army choice | `+0x31` | byte | `IsAllowedToStart` |
| army choice | `+0x32` | byte | `IsAllowedCustomDraft`: 1 shows the screen where the player builds an army |
| set army | `+0x00` | list | the machines: pointers to machine data, in army order |
| set army | `+0x10` | list | starting squares, 0x10 bytes each |
| machine collection | `+0x20` | list | `AllCollectableUnitTypes` |
| machine collection | `+0x30` | int32 | `MaxCountPerUnitType` |
| machine collection | `+0x34` | int32 | `MaxDraftPointCount` |
| machine collection | `+0x38` | int32 | `MinPointsRequired` |
| saved collection | `+0x28` | list | `DraftPresets`: the saved armies |
| saved collection | `+0x38` | int32 | `SelectedPreset`: which saved army is picked |
| saved army | `+0x28` | list | `DraftedUnits`, 0x20 bytes each |
| saved army entry | `+0x08` | 16 bytes | the machine's ID |
| saved army entry | `+0x18` | int32 | how many of it |
| scripted move | `+0x00` | byte | the action |
| scripted move | `+0x08` | int32 | x |
| scripted move | `+0x0C` | int32 | y |
| scripted move | `+0x10` | byte | facing |
| scripted move | `+0x11` | byte | not known |
| starting square | `+0x00` | int32 | x |
| starting square | `+0x04` | int32 | y |
| starting square | `+0x08` | byte | direction |
| placing controller | `+0x10` | ptr | the player it places for |
| placing controller | `+0x20` | int32 | 0 before a machine is picked, 1 once its square is chosen, 2 once confirmed |
| placing controller | `+0x28` | ptr | the machine on the cursor |
| placing controller | `+0x30` | float | step timer: a huge number (1e9) holds the next step, 0 lets it go |
| playing controller | `+0x00` | ptr | vtable: shows which side's controller it is |
| playing controller | `+0x30` | list | actions |
| playing controller | `+0x48` | ptr | its `+0x08` is the match |
| playing controller | `+0x50` | ptr | the player it plays for |
| playing controller | `+0x80` | int32 | the selected square: x |
| playing controller | `+0x84` | int32 | the selected square: y |
| playing controller | `+0x88` | ptr | the machine being used |
| playing controller | `+0xA0` | int32 | the square the latest action started from: x |
| playing controller | `+0xA4` | int32 | the square the latest action started from: y |
| playing controller | `+0xA8` | int32 | the square the latest action went to: x |
| playing controller | `+0xAC` | int32 | the square the latest action went to: y |
| playing controller | `+0xB0` | byte | ASSUMED: the facing when the machine was activated; never the facing an action chose |
| playing controller | `+0xE8` | ptr | the movement plan: `+0x00` how many steps, `+0x08` the steps |
| playing controller | `+0x320` | byte | 1 while a machine is activated |
| playing controller | `+0x348` | byte | the computer's busy flag: set while a scripted move is running |
| playing controller | `+0x350` | ptr | the computer's thinking object, while it thinks |
| playing controller | `+0x440` | int32 | waiting actions: how many |
| playing controller | `+0x448` | ptr | waiting actions: the list |
| playing controller | `+0x450` | int32 | waiting actions: which one runs next |
| thinking object | `+0xF0` | byte[4] | the decision: action, from square, to square, 0 |
| thinking object | `+0xF4` | byte | read along with the decision |
| thinking object | `+0xFC` | byte | done flag |
| thinking object | `+0x100` | int32 | job state |
| thinking object | `+0xE0` to `+0x11F` | 64 bytes | read whole for inspection once it is done |
| text block | `-0x10` | uint32 | how many things use it; `0x80000001` on the shared copy |
| text block | `-0x0C` | uint32 | an ID number made from the text (a hash); `0xFFFFFFFF` means not set |
| text block | `-0x08` | uint32 | the length |
| text block | `-0x04` | uint32 | the room: the block's size minus one; on the shared copy it equals the length |
| text object | `+0x00` | float[4] | colour: red, green, blue, see-through |
| text object | `+0x30` | ptr | the text |
| text object | `+0x50` | float | font size |

## Memory manager

| Name | Where | What it is |
| --- | --- | --- |
| main memory manager | `[base + 0x897A388]` | the object that hands out memory |
| hand out memory | its vtable `+0x88` | `rcx` the manager, `rdx` the size |
| reserved area start | `[base + 0x1FE0510]` | read `0x26DC0000000` |
| reserved area end | `[base + 0x1FE0518]` | read `0x271C0000000`, 16 GB after the start |
| memory handed out | | comes from outside the reserved area and is not cleared; the game gives it back to the right manager, but gives memory from anywhere else to the wrong one |

## Values

| Terrain | Players see | The game's name |
| --- | --- | --- |
| `-3` | Blight | Blight |
| `-2` | Chasm | Void |
| `-1` | Marsh | Water |
| `0` | Grassland | Plains |
| `1` | Forest | Forest |
| `2` | Hills | Hills |
| `3` | Mountains | Mountains |

| Facing | Direction |
| --- | --- |
| `0` | north, towards low y |
| `1` | east |
| `2` | south |
| `3` | west |

| Scripted move action | Meaning |
| --- | --- |
| `0` | activate |
| `1` | Overcharge |
| `2` | move |
| `3` | attack; the square is where the attacker stands |
| `4` | end turn |

| Attack type | Name in the code | Shown in the game as |
| --- | --- | --- |
| `0` | Strike | Melee |
| `1` | Shot | Gunner |
| `2` | Dash | Dash |
| `3` | Ram | Ram |
| `4` | Dive | Swoop |
| `5` | Tow | Pull |

| Skill | Name in the code | Shown in the game as | Skill | Name in the code | Shown in the game as |
| --- | --- | --- | --- | --- | --- |
| `0` | none | none | `9` | Freeze | Freeze |
| `1` | Roam | Gallop | `10` | Seed | Growth |
| `2` | Stalk | Stalk | `11` | Unearth | Alter Terrain |
| `3` | Scurry | Climb | `12` | Blind | Blind |
| `4` | Climb | High Ground | `13` | Enpower | Empower |
| `5` | Spread | Sweep | `14` | Stun | Drain |
| `6` | Shield | Shield | `15` | Spill | Spray |
| `7` | Retaliate | Retaliate | `16` | Confuse | Whiplash |
| `8` | Burn | Burn | | | |

| Placing area | Rows |
| --- | --- |
| seat 1 | `0` to `depth - 1` |
| seat 0 | `height - depth` to `height - 1` |

| Text searched for | Where it is |
| --- | --- |
| `ALOY` | the left corner name |
| `OPPONENT` | the right corner name |
| `Opponent's Turn` | the turn banner |
| `Beginner's Set` | the army name on the screen before a match |

## Markers written into the game

| Marker | Where |
| --- | --- |
| `SCOMRING` (`0x474E49524D4F4353`) | at `+0x00` of the action code block |
| `STRKGRNT` (`0x544E52474B525453`) | at `+0x20` of a 0x30-byte set-army list the game's memory manager handed out; the one set army is at `+0x00` |
| `STRKSET` and a zero byte | straight after the zero byte that ends a rewritten army name |

## Challenges

| ID (as stored in memory) | Challenge |
| --- | --- |
| `8BBC182B83FC495AA2150021217530D5` | Beginner's Practice: Easy |
| `1E46038FF9804646813C959A31EAFB91` | Beginner's Practice: Medium |
| `74771C9B66D9441AA4CBD5E47D4851AD` | Beginner's Practice: Hard |
| `0ECEA5D9B9F841908D9716A1421F0AEC` | Regular Challenge |

## Machines

| Machine | ID (as stored in memory) | Cost | Health | Move | Range | Power | Attack type | Skill |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Burrower | `1C96A39FFE37791F8AE07BD49A2230FF` | 1 | 4 | 2 | 1 | 2 | Strike | none |
| Grazer | `2B34B3566FC1ED50071517AE89C5252F` | 1 | 4 | 2 | 1 | 1 | Ram | Roam |
| Leaplasher | `76D3C3711B67863E82D8C703AAB1A815` | 1 | 3 | 4 | 1 | 1 | Strike | Enpower |
| Scrounger | `B78EF94227B7D36454715138E9A17144` | 1 | 5 | 3 | 1 | 2 | Strike | none |
| Spikesnout | `44333B622E88297526F3BAEFE049E1E6` | 1 | 5 | 2 | 1 | 2 | Strike | none |
| Bristleback | `C5A08FEFF4757D3A3BD4A614A0B5E948` | 2 | 4 | 3 | 1 | 2 | Ram | Spill |
| Charger | `FDC39FDFF4CE9C5B8190CE46AAFCF7B5` | 2 | 4 | 3 | 2 | 2 | Dash | Roam |
| Fanghorn | `EC1B65E8878813E8A601E253EAA2913E` | 2 | 5 | 2 | 2 | 2 | Ram | Climb |
| Glinthawk | `4726C13DD5722AF80595854DA7E2FCBF` | 2 | 5 | 3 | 3 | 2 | Dive | none |
| Lancehorn | `D648F85DD50802FA13A3AF73EC249DF1` | 2 | 5 | 2 | 2 | 2 | Ram | Scurry |
| Longleg | `FBD88CC00C71FC3A1C9606BF56DF79B7` | 2 | 6 | 4 | 2 | 1 | Shot | Enpower |
| Plowhorn | `A70D3B57757E0A4692DF0D4511231EDE` | 2 | 5 | 2 | 1 | 2 | Ram | Seed |
| Scrapper | `52739299954A231229FD4BE0818ADBB0` | 2 | 5 | 2 | 2 | 3 | Shot | none |
| Skydrifter | `36791A8338E8498ACD11B127EB75CF82` | 2 | 6 | 2 | 3 | 2 | Dive | none |
| Tracker Burrower | `166DBB122F7176A0028C7DB5BE42BDEC` | 2 | 4 | 2 | 1 | 2 | Strike | Unearth |
| Bellowback | `823A38C44964F6F8CF8E022B64A29FA6` | 3 | 7 | 2 | 2 | 3 | Shot | Spill |
| Clawstrider | `ED68990FF5589A2DA853F0D163682C40` | 3 | 8 | 2 | 2 | 3 | Strike | none |
| Redeye Watcher | `0E44B98882BA9AFD876C0DB6144D35F5` | 3 | 5 | 2 | 2 | 2 | Shot | Blind |
| Shell-Walker | `05662ED22BB56D93305986970E81E6C4` | 3 | 7 | 2 | 1 | 2 | Strike | Shield |
| Snapmaw | `4B962F6770CF0C4B905C3E2782E46B24` | 3 | 7 | 2 | 3 | 3 | Tow | none |
| Sunwing | `D7570C9AAEC2D94C677452D1861A99DA` | 3 | 7 | 3 | 2 | 3 | Dive | none |
| Widemaw | `7BDE91066BA0DB7EFFB78AC3CFE1B5BF` | 3 | 7 | 2 | 2 | 3 | Tow | none |
| Clamberjaw | `8960A49889D0783EF8EDCE2CC08C0BCE` | 4 | 8 | 3 | 1 | 3 | Strike | Stalk |
| Elemental Clawstrider | `AE1497611CC146BCBEC792BA79E94C0C` | 4 | 8 | 2 | 2 | 3 | Shot | Burn |
| Ravager | `8218A7A4485CED3F9DBFF00447CA94A0` | 4 | 9 | 2 | 2 | 2 | Shot | Spread |
| Rollerback | `EC24DE233B69D5702EC68D058C0DF9ED` | 4 | 5 | 3 | 2 | 3 | Strike | Retaliate |
| Stalker | `0D28E4466864E92033A848F03ED49A69` | 4 | 5 | 3 | 2 | 4 | Strike | Stalk |
| Waterwing | `BA426B8061A7441283CB2D74EFE8F26F` | 4 | 8 | 3 | 2 | 2 | Tow | Confuse |
| Apex Clawstrider | `05EDB2988A7B9D03733EC774E2491915` | 5 | 8 | 2 | 1 | 3 | Strike | Retaliate |
| Behemoth | `370A5C1EE3F50A95FE7A04BE068355FC` | 5 | 10 | 2 | 2 | 3 | Shot | Shield |
| Bilegut | `455F58E56AA6405EA67613ADBE69E737` | 5 | 9 | 2 | 3 | 3 | Tow | Unearth |
| Dreadwing | `435534A445562BA16633AF4B908D83B2` | 5 | 9 | 2 | 3 | 3 | Dive | Confuse |
| Tremortusk | `23C2AA3CCB2680F418CA1D7E9F179E9D` | 5 | 10 | 2 | 2 | 3 | Dash | Spread |
| Rockbreaker | `ADEA18E33DA2CAF15010D29CA12FE1F3` | 6 | 9 | 3 | 2 | 3 | Shot | Unearth |
| Shellsnapper | `C730992552952A2BA35C975435C03F37` | 6 | 10 | 2 | 3 | 3 | Tow | none |
| Stormbird | `7875B4B22E79B8BF0996D4B74BCC0477` | 6 | 9 | 3 | 3 | 3 | Dive | Spread |
| Thunderjaw | `F9433C1448F8F052BD457978CD0BFEC5` | 6 | 10 | 3 | 2 | 3 | Dash | Spread |
| Tideripper | `C6E4562880DBB8F7080473258C8D1F6C` | 6 | 10 | 2 | 3 | 4 | Tow | none |
| Fireclaw | `D132E56AAEF7AC7F24DBC79886876512` | 7 | 10 | 3 | 2 | 4 | Strike | Burn |
| Frostclaw | `373D8F477EAEAF6BE80EFF08610BBA8F` | 7 | 10 | 2 | 2 | 4 | Strike | Freeze |
| Scorcher | `E1301D6553C24EDD4CFBE9E21EC99D1C` | 8 | 12 | 2 | 2 | 4 | Dash | Burn |
| Slitherfang | `F5172B344148EE7E5DB47BD7A23AF9F9` | 9 | 12 | 2 | 3 | 4 | Dash | Unearth |
| Slaughterspine | `82F13F6222FCF687A5684CC1F9B96F4F` | 10 | 15 | 2 | 1 | 4 | Strike | Spill |
