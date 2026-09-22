# LBA2 game flags (`ListVarGame`)

Little Big Adventure 2 keeps its story in one array of **256 signed 16-bit values**, `ListVarGame[0..255]` (`MAX_VARS_GAME`). It is saved with the game (`SAVEGAME.CPP`, first 512 bytes of the context) and is read and written by the life scripts of every scene. Unlike LBA1, where a game flag is a yes/no, an LBA2 flag is usually a **number**: a stage of a quest, a count, a track label, or a bitmask, so the meaning of a value matters as much as the index.

This list was compiled from four sources, and every entry says which one it rests on:

- **LBATrainer** `quests.xml` (names and the values its author observed for indices 40-255). Index = `(memoryOffset - 0x57BF3) / 2`; that mapping is confirmed against the trainer's own inventory list (Holomap = 0, Darts = 2, Blowgun = 23).
- **The engine source** (`COMMON.H` `FLAG_*` constants, `GERELIFE.CPP`, `PERSO.CPP`, `INVENT.CPP`, `PLAYACF.CPP`, `WAGON.CPP`, `3DEXT/DECORS.CPP`, ...): what the engine itself does with an index.
- **Every retail life script**: all 222 scenes of the original `SCENE.HQR` (6,000+ scripts) were decoded and each `SET_VAR_GAME`, `ADD_VAR_GAME`, `SUB_VAR_GAME`, `LF_VAR_GAME` test, `SWITCH`/`CASE`, `TRACK_TO_VAR_GAME`, `VAR_GAME_TO_TRACK`, `FOUND_OBJECT`, `STATE_INVENTORY` and `USE_INVENTORY` was recorded with its enclosing conditions and nearby dialogue (5,435 events).
- **The island files** (`*.ILE`): a decor object hides or shows itself from a flag (`Beta >> 16`; positive = hidden while set, negative = visible only while set). 189 decors depend on a flag.

Each entry's `source` says where its name comes from: `curated` = named from the engine source and the scripts (the trainer's name, where there was one, was cross-checked against the values and scenes the scripts use, then kept or corrected); `trainer+curated` = the trainer's name and values were kept after the same cross-check, with notes added; `trainer` = the trainer's entry unchanged. The *Written by / Read in* lines are always mined from the scripts and are exact.

Scene numbers are `SCENE.HQR` numbers (entry - 1) written `s27`. Scenes 193 and up are the demo's copies; flags used only there are marked **demo only**.

## How the engine uses the array

| Mechanism | Detail |
|---|---|
| Life-script writes | `SET_VAR_GAME(var, value)`, `ADD_VAR_GAME`, `SUB_VAR_GAME` (saturating at the S16 limits). Writing var 8 also updates `NbGoldPieces` / `NbZlitosPieces`. |
| Life-script reads | `LF_VAR_GAME(var)` (S16), `LF_CHAPTER` (= var 253), `LF_USE_INVENTORY(item)` (var = item slot), `LF_NB_GOLD_PIECES`. |
| Inventory | slots 0-39 (`MAX_INVENTORY`) are `ListVarGame[slot]`; a quantity for the countable ones (`FLAG_INV_FEWER`: darts, kashes, penguins, gems), otherwise 0/1. `FOUND_OBJECT`, `STATE_INVENTORY` and `SET_USED_INVENTORY` take the slot number. |
| Track labels | `TRACK_TO_VAR_GAME(var)` stores the actor's current track label, `VAR_GAME_TO_TRACK(var)` sends the actor to that label. Vars 40 (Zoe) and 80 (the wizard pedlar) are used this way. |
| Decor visibility | island decors carry a var in the high 16 bits of `Beta`; the engine re-evaluates them when a cube loads (`FixeObjetsDecorsInvisibles`). |
| Movies | vars 235-237 are a 48-bit mask of the ACF cut scenes already played. |
| Cheats / console | the native engine's console has `vargame <n> [value]`, `flags [all]` (dumps the named flags) and `give <item> [n]`. |

## Index

Kinds: `inventory` item slot (0/1), `count` quantity, `quest` several stages, `flag` on/off, `track` an NPC's track label, `bits` bitmask, `decor` mainly gates island decor, `engine` used by engine code, `unused`.

| # | Symbol | Kind | Name |
|--:|---|---|---|
| 0 | `FLAG_HOLOMAP` | inventory | Holomap |
| 1 | `FLAG_BALLE_MAGIQUE` | inventory | Magic ball |
| 2 | `FLAG_DART` | count | Darts |
| 3 | `FLAG_BOULE_SENDELL` | inventory | Sendell's ball |
| 4 | `FLAG_TUNIQUE` | inventory | Tunic and Sendell's medallion |
| 5 | `FLAG_PERLE` | inventory | Pearl of Incandescence / Itinerary token (one shared slot) |
| 6 | `FLAG_CLEF_PYRAMID` | inventory | Pyramid-shaped key |
| 7 | `FLAG_VOLANT` | inventory | Part for the car (steering wheel) |
| 8 | `FLAG_MONEY` | count | Kashes (Twinsun) / Zlitos (Zeelich) |
| 9 | `FLAG_PISTOLASER` | inventory | Pisto-Laser |
| 10 | `FLAG_SABRE` | inventory | Emperor's sabre |
| 11 | `FLAG_GANT` | inventory | Wannie's glove |
| 12 | `FLAG_PROTOPACK` | inventory | Proto-Pack |
| 13 | `FLAG_TICKET_FERRY` | inventory | Ferry ticket |
| 14 | `FLAG_MECA_PINGOUIN` | count | Nitro-Meca-Penguins |
| 15 | `FLAG_GAZOGEM` | inventory | Can of GazoGem |
| 16 | `FLAG_DEMI_MEDAILLON` | inventory | Dissidents' ring (half medallion) |
| 17 | `FLAG_ACIDE_GALLIQUE` | inventory | Gallic acid |
| 18 | `FLAG_CHANSON` | inventory | Ferryman's song |
| 19 | `FLAG_ANNEAU_FOUDRE` | inventory | Ring of lightning |
| 20 | `FLAG_PARAPLUIE` | inventory | Customer's umbrella |
| 21 | `FLAG_GEMME` | count | Gems |
| 22 | `FLAG_CONQUE` | inventory | Horn of the Blue Triton |
| 23 | `FLAG_SARBACANE` | inventory | Blowgun |
| 24 | `FLAG_DISQUE_ROUTE / FLAG_VISIONNEUSE` | inventory | Memory viewer (ACF viewer) |
| 25 | `FLAG_TART_LUCI` | inventory | Slice of firefly tart |
| 26 | `FLAG_RADIO` | inventory | Portable radio |
| 27 | `FLAG_FLEUR` | inventory | Garden Balsam |
| 28 | `FLAG_ARDOISE` | inventory | Magic slate |
| 29 | `FLAG_TRADUCTEUR` | inventory | Translator |
| 30 | `FLAG_DIPLOME` | inventory | Wizard's diploma |
| 31 | `FLAG_DMKEY_KNARTA` | inventory | Key fragment of the Francos |
| 32 | `FLAG_DMKEY_SUP` | inventory | Key fragment of the Sups |
| 33 | `FLAG_DMKEY_MOSQUI` | inventory | Key fragment of the Mosquibees |
| 34 | `FLAG_DMKEY_BLAFARD` | inventory | Key fragment of the Wannies |
| 35 | `FLAG_CLE_REINE` | inventory | Key to the queen's passage |
| 36 | `FLAG_PIOCHE` | inventory | Pick-axe |
| 37 | `FLAG_CLEF_BOURGMESTRE` | inventory | Burgomaster's key |
| 38 | `FLAG_NOTE_BOURGMESTRE` | inventory | Burgomaster's notes |
| 39 | `FLAG_PROTECTION` | inventory | Protective spell |
| 40 | `FLAG_SCAPHANDRE (unused)` | track | Zoe position (track label) |
| 41 |  | quest | Hacienda women's steam room |
| 42 |  | flag | Dino-Fly injured status |
| 43 |  | quest | Package centre |
| 44 |  | flag | Waiter in tavern cellar |
| 45 |  | quest | Citadel shop keeper status |
| 46 |  | flag | Citadel shop cash register |
| 47 |  | quest | Injured Wannie |
| 48 |  | flag | Throne door |
| 49 |  | flag | Package centre lower door |
| 50 |  | quest | Package centre hidden money box |
| 51 |  | quest | Stopping the rain |
| 52 |  | decor | Citadel decor hidden by a flag nothing sets |
| 53 |  | flag | Tralu: first door / switch |
| 54 |  | quest | Pyramid key quest |
| 55 |  | flag | All four key fragments combined |
| 56 |  | quest | Lighthouse keeper / alien landing progress |
| 57 |  | quest | Stolen umbrella |
| 58 |  | unused | Unused |
| 59 |  | quest | Ferry ticket / crossing |
| 60 |  | quest | Tralu boss fight |
| 61 |  | quest | Becoming a wizard |
| 62 |  | quest | Cure Dino-Fly |
| 63 |  | quest | School of Magic: blowgun test |
| 64 |  | quest | Ferryman's song |
| 65 |  | decor | School of magic entrance doors |
| 66 |  | quest | Map in neighbour's house |
| 67 | `-` | quest | Wannies island: hiding in a transport box |
| 68 |  | flag | Left Twinsen's house: Zoe cut scene |
| 69 |  | quest | Mosquibee rebellion progress |
| 70 |  | flag | Alien cut scene after the rain |
| 71 |  | quest | Citadel taxi (Lupin-Bourg bike) |
| 72 |  | quest | Attacked children in the school |
| 73 |  | flag | Mosquibee blowtron |
| 74 |  | quest | Fix the car / get the horn |
| 75 |  | unused | Unused |
| 76 |  | flag | Door to fragment in the mine |
| 77 |  | quest | Grobo brother waypoint |
| 78 |  | flag | Grobo brother spawned |
| 79 |  | quest | Funfrock pizza scene |
| 80 |  | track | Wizard pedlar position (track label) |
| 81 |  | quest | Wizard to Otringal |
| 82 |  | flag | Ferryman taxi ride |
| 83 |  | quest | Main story sub-stage (wizard, Baldino, return to Citadel, Sendell) |
| 84 |  | quest | Otringal prison: mosquibee cell |
| 85 |  | quest | Emerald Moon / Baldino escape |
| 86 |  | flag | Killed the green alien on the spaceship / guards triggered |
| 87 |  | flag | Otringal prison: Joe status |
| 88 |  | bits | Otringal prison: guards killed (bitmask) |
| 89 |  | flag | On the moving platform in the Temple of Bu |
| 90 |  | flag | Emerald Moon: entrance panel toggle |
| 91 |  | flag | Wannies mine: object on the plate |
| 92 |  | bits | Otringal control tower: people killed (bitmask) |
| 93 |  | flag | Emerald Moon: yellow/black doors |
| 94 |  | quest | Dino-Fly location |
| 95 |  | flag | Read the wizard's note about finishing the spell |
| 96 |  | unused | Unused |
| 97 |  | flag | Emerald Moon: circle door |
| 98 |  | flag | Emerald Moon: triangle door |
| 99 |  | flag | Emerald Moon: square door |
| 100 |  | flag | Emerald Moon: spaceship landed |
| 101 |  | decor | Otringal fence panel 4 destroyed |
| 102 |  | decor | Otringal fence panel 2 destroyed |
| 103 |  | decor | Otringal fence panel 1 destroyed |
| 104 |  | decor | Otringal fence panel 5 destroyed |
| 105 |  | decor | Otringal fence panel 6 destroyed |
| 106 |  | decor | Otringal fence panel 3 destroyed |
| 107 |  | quest | Undergas elevator |
| 108 |  | quest | Harbour boat animation state (Citadel and Desert) |
| 109 |  | flag | Wizard's outfit |
| 110 |  | decor | Otringal decor hidden outside chapters 6-8 |
| 111 |  | flag | Bu poster in the Citadel ticket office |
| 112 |  | flag | Otringal: guards recognise Twinsen and attack |
| 113 |  | flag | Otringal casino: jackpot room state |
| 114 |  | unused | Unused |
| 115 |  | track | Otringal lowest elevators: which platform carries Twinsen |
| 116 |  | quest | Otringal lift / safari giraffe position |
| 117 |  | flag | Baldino gave the super jet-pack |
| 118 |  | flag | Desert island: healed the petanque player |
| 119 |  | flag | Zoe by the car: Citadel cube 9 decor |
| 120 |  | flag | Francos gazogem factory: life container taken |
| 121 |  | flag | Used the telescope on the hacienda roof |
| 122 |  | flag | Leontine taxi |
| 123 |  | flag | Taxi to the Francos island available |
| 124 |  | quest | Path to the Otringal rebels |
| 125 |  | flag | Yellow taxi: Celebration island to Francos |
| 126 |  | flag | Yellow taxi: Francos to Otringal |
| 127 |  | flag | Gave the gazogem to Baldino |
| 128 |  | flag | Mosquibee arrival scene: actor 10 reached track 11 |
| 129 |  | flag | Boarded the boat to the elevator |
| 130 |  | quest | Laser pistol quest |
| 131 |  | quest | Otringal bar show |
| 132 |  | flag | Johnny Rocket position |
| 133 |  | decor | Otringal decor hidden before chapter 6 |
| 134 |  | bits | Wannies mine, 2nd room: gems collected (bitmask 1, 2, 4) |
| 135 |  | flag | Wannies mine, 3rd room: gem |
| 136 |  | bits | Wannies island near the temple: gems collected (bitmask 1, 2) |
| 137 |  | bits | Mosquibee island arrival: gems (bitmask 1, 2) |
| 138 |  | bits | Mosquibee island arrival: gems near the bridge (bitmask 1, 2) |
| 139 |  | flag | Mosquibee gem in the spider area |
| 140 |  | bits | Island under Celebration arrival: gems (bitmask 1, 2) |
| 141 |  | bits | Island under Celebration upper scene: gems (bitmask 1, 2, 4) |
| 142 |  | flag | Island under Celebration hide-out gem |
| 143 |  | flag | Carried by the turtle |
| 144 |  | flag | Otringal souvenir shop: wizard diploma sold |
| 145 |  | flag | Otringal souvenir shop: ferry ticket sold |
| 146 |  | flag | Otringal souvenir shop: umbrella sold |
| 147 |  | flag | Burgomaster's notes found |
| 148 |  | quest | Sup fragment / emperor's palace |
| 149 |  | decor | Desert island: extra props from chapter 4 |
| 150 |  | flag | Mosquibee island: Twinsen climbing the rope |
| 151 |  | flag | Francos arrival flag |
| 152 |  | quest | Emperor's shuttle |
| 153 |  | flag | Told the grand rector about the pearl and Sendell's ball |
| 154 |  | flag | Asked where the pearl was and was told: in the clam |
| 155 |  | flag | Told the rector about the pearl of incandescence |
| 156 |  | decor | Otringal upper city: emperor's shuttle decor |
| 157 | `PLAY_THE_END` | quest | The End: Dark Monk destroyed |
| 158 |  | flag | Looked at the alien spaceship (cut scene) |
| 159 |  | quest | Funfrock button and the children |
| 160 |  | flag | Emperor triggered the Moon |
| 161 |  | quest | Joe's shell |
| 162 |  | bits | Dark Monk statue 1st scene: teleporters destroyed (bitmask) |
| 163 |  | flag | Dark Monk statue: top teleporter destroyed |
| 164 |  | decor | Citadel sewer cover A (cube 5) |
| 165 |  | decor | Citadel sewer cover B (cube 7) |
| 166 |  | decor | Citadel cube 6: alien prop during chapter 2 |
| 167 |  | decor | Desert cube 6: harbour ferry prop |
| 168 |  | flag | Desert: tour / car-jump gate |
| 169 |  | flag | Pearl of Incandescence found |
| 170 |  | flag | Itinerary token held (Esmer shuttle ticket) |
| 171 |  | flag | As a wizard, alien asks to visit their planet |
| 172 |  | flag | Read only by two desert actors |
| 173 |  | decor | Citadel cube 3 decor |
| 174 |  | flag | Attacked the guard in the Otringal tower on returning to the Citadel |
| 175 |  | quest | Temple of Bu puzzle stage |
| 176 |  | flag | Bu Meca-Penguin taken from the statue plinth |
| 177 |  | flag | Dark Monk statue 3rd scene: switch 1 |
| 178 |  | flag | Dark Monk statue 3rd scene: switch 2 |
| 179 |  | flag | Dark Monk statue 3rd scene: switch 3 |
| 180 |  | flag | Dark Monk statue 3rd scene: teleporter destroyed |
| 181 |  | flag | Dark Monk statue 3rd scene: switch 5 |
| 182 |  | decor | Desert cube 4: coast decor |
| 183 |  | decor | Desert cube 12: coast decor |
| 184 |  | decor | Desert cube 16: coast decor |
| 185 |  | decor | Desert cube 1: coast decor |
| 186 |  | decor | Desert cube 17: coast decor |
| 187 |  | decor | Desert cube 2: coast decor |
| 188 |  | decor | Desert cube 3: coast decor |
| 189 |  | decor | Desert cube 7: coast decor |
| 190 |  | decor | Desert cube 13: decor |
| 191 |  | flag | Wannies mine: gem near the clover box |
| 192 |  | flag | Wannies mine: gem |
| 193 |  | count | Wannies island temple gems taken |
| 194 |  | flag | Citadel: spaceship / Joe scene at the Sendell sign |
| 195 |  | flag | Dark Monk statue: wizards hole |
| 196 | `-` | unused | Unused |
| 197 | `-` | unused | Unused |
| 198 | `-` | unused | Unused |
| 199 | `-` | unused | Unused |
| 200 | `-` | unused | Unused |
| 201 | `-` | unused | Unused |
| 202 | `-` | unused | Unused |
| 203 | `-` | unused | Unused |
| 204 | `-` | unused | Unused |
| 205 | `-` | unused | Unused |
| 206 | `-` | unused | Unused |
| 207 | `-` | unused | Unused |
| 208 | `-` | unused | Unused |
| 209 | `-` | unused | Unused |
| 210 | `-` | unused | Unused |
| 211 | `-` | unused | Unused |
| 212 | `-` | unused | Unused |
| 213 | `-` | unused | Unused |
| 214 | `-` | unused | Unused |
| 215 | `-` | unused | Unused |
| 216 | `-` | unused | Unused |
| 217 | `-` | unused | Unused |
| 218 | `-` | unused | Unused |
| 219 | `-` | unused | Unused |
| 220 | `-` | unused | Unused |
| 221 | `-` | unused | Unused |
| 222 | `-` | unused | Unused |
| 223 | `-` | unused | Unused |
| 224 | `-` | unused | Unused |
| 225 | `-` | unused | Unused |
| 226 | `-` | unused | Unused |
| 227 | `-` | unused | Unused |
| 228 | `-` | unused | Unused |
| 229 | `-` | unused | Unused |
| 230 | `-` | unused | Unused |
| 231 | `-` | unused | Unused |
| 232 | `-` | unused | Unused |
| 233 | `-` | unused | Unused |
| 234 | `-` | unused | Unused |
| 235 | `FLAG_ACF` | engine | Movies seen: bits 0-15 of the ACF list |
| 236 | `FLAG_ACF2` | engine | Movies seen: bits 16-31 |
| 237 | `FLAG_ACF3` | engine | Movies seen: bits 32-47 |
| 238 | `-` | unused | Unused |
| 239 | `-` | unused | Unused |
| 240 |  | flag | Clover box: Citadel sewers |
| 241 |  | flag | Clover box: Dome of the Slate |
| 242 |  | flag | Clover box: Wannies mine 2nd room (read only) |
| 243 |  | flag | Clover box: Desert island (via the turtle) |
| 244 |  | flag | Clover box: Island under Celebration and Island CX |
| 245 |  | flag | Clover box: Emerald Moon |
| 246 |  | flag | Clover box: island across from the hacienda |
| 247 | `-` | unused | Unused |
| 248 |  | flag | Demo only *(demo only)* |
| 249 | `FLAG_ESC` | engine | Scenario signal: cut scene skipped with ESC |
| 250 | `-` | unused | Unused |
| 251 | `FLAG_CLOVER` | count | Clover leaves (current life boxes) |
| 252 | `FLAG_VEHICULE_PRIS` | engine | Vehicle taken |
| 253 | `FLAG_CHAPTER` | engine | Chapter |
| 254 | `FLAG_PLANETE_ESMER` | engine | Esmer rails (mine wagon uses Esmer rail bricks) |
| 255 | `FLAG_DONT_USE` | engine | Reserved (inventory sentinel) |

## Inventory (0-39)

One slot per item. 0 = not owned, 1 = owned unless a count is noted. Several slots are reused by a second item in a later chapter (the icon changes with `STATE_INVENTORY`).

### 0 - Holomap

`inventory` - `FLAG_HOLOMAP` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** Found in Twinsen's secret room (s1); sold for 10 kashes in the Citadel shop (s14) and the Desert bazaar (s36). The engine only opens the holomap when this is 1 (PERSO.CPP) and SET_HOLO_POS pops the inventory icon up.
- **Written by:** s1 Citadel Island, Twinsen's house Secret scene (found-object, =1); s14 Citadel Island, Shop (found-object, =1); s36 White Leaf Desert, Bazaar (found-object)
- **Read in 4 scene(s):** s1, s3, s14, s36

### 1 - Magic ball

`inventory` - `FLAG_BALLE_MAGIQUE` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** Found in Twinsen's secret room (s1). Its power/model follows MagicLevel, not this value (INVENT.CPP).
- **Written by:** s1 Citadel Island, Twinsen's house Secret scene (found-object, =1)
- **Read in 4 scene(s):** s1, s2, s15, s43

### 2 - Darts

`count` - `FLAG_DART` - source: curated

- **Notes:** Quantity, capped at 99 (MAX_DARTS). Sold three for 4 kashes (s14, s36, s67); the engine subtracts one per throw (FICHE.CPP) and adds pickups (DART.CPP).
- **Written by:** s1 Citadel Island, Twinsen's house Secret scene (found-object); s14 Citadel Island, Shop (found-object, =3); s36 White Leaf Desert, Bazaar (found-object, =3); s67 White Leaf Desert, at the Temple of Bù (found-object)
- **Read in 4 scene(s):** s1, s14, s36, s67

### 3 - Sendell's ball

`inventory` - `FLAG_BOULE_SENDELL` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** Taken in Sendell's ball room (s34). The school/desert scenes read it to know the ball was recovered.
- **Written by:** s34 Citadel Island, Sendell's ball room (found-object, =1)
- **Read in 8 scene(s):** s24, s27, s34, s39, s47, s56, s57, s113

### 4 - Tunic and Sendell's medallion

`inventory` - `FLAG_TUNIQUE` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** Taken in Miss Bloop's museum (s15). While 0 the hero uses the no-tunic body (OBJECT.CPP). STATE_INVENTORY(4,1) turns the icon into the wizard's tunic when the wizard outfit is bought (var 109).
- **Written by:** s15 Citadel Island, Miss Bloop's Private Museum (=1, found-object); s62 White Leaf Desert, near car jump (icon state 1, found-object); s63 White Leaf Desert, near Temple of Bù (icon state 1, found-object); s65 White Leaf Desert, near Esmer Shuttle (icon state 1, found-object); s66 White Leaf Desert, near Bald Mountain (icon state 1, found-object); s67 White Leaf Desert, at the Temple of Bù (icon state 1, found-object); ... (5 more scenes)
- **Read in 4 scene(s):** s0, s2, s15, s48

### 5 - Pearl of Incandescence / Itinerary token (one shared slot)

`inventory` - `FLAG_PERLE` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** TabInv[5] holds model 5 (pearl) and model 24 (itinerary token, "FLAG_DISQUE_ROUTE"). Pearl: taken in the pearl cave (s113), handed to the Weather Wizard (s21). Token: picked up in the Esmer base (s12) and the Otringal control tower (s85), consumed by the Esmer shuttles (s41, s114, s182). See also 169 and 170.
- **Written by:** s12 White Leaf Desert, Esmer Base (=1, icon state 1, found-object, marked used); s21 Citadel Island, Tent of the Weather Wizard (=0); s41 White Leaf Desert, Esmer Shuttle (=0); s85 Otringal, control tower entrance (=1, icon state 1, found-object); s113 White Leaf Desert, Pearl of Incandescence room (=1, icon state 0, found-object); s114 White Leaf Desert, Esmer shuttle (=0); ... (2 more scenes)
- **Read in 7 scene(s):** s12, s21, s41, s85, s114, s177, s182

### 6 - Pyramid-shaped key

`inventory` - `FLAG_CLEF_PYRAMID` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** Given by the neighbour after the gallic-acid quest (s9, var 54); used on the sewer door (s17).
- **Written by:** s9 Citadel Island, Neighbor's house (found-object, =1); s17 Citadel Island, Sewer (below Downtown) (=0)
- **Read in 3 scene(s):** s9, s17, s57

### 7 - Part for the car (steering wheel)

`inventory` - `FLAG_VOLANT` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** Found in Baldino's house (s38); consumed at the Dino-Fly (s49). Doubles as the translator's slot (see 29).
- **Written by:** s38 White Leaf Desert, J. Baldino's House (found-object, =1); s49 Citadel Island, near Dino-Fly (=0)
- **Read in 1 scene(s):** s49

### 8 - Kashes (Twinsun) / Zlitos (Zeelich)

`count` - `FLAG_MONEY` - source: curated

- **Notes:** Quantity. Mirrored into NbGoldPieces or NbZlitosPieces (chosen by Planet: Zlitos from Planet 2, the Zeelich islands such as Otringal; the Emerald Moon still pays in Kashes) whenever a script writes it, and copied back by InitTabIndir. Scripts spend it with SUB_VAR_GAME(8, n) and GIVE_GOLD_PIECES; amounts seen 1 to 10000.
- **Written by:** s3 Citadel Island, Tavern (-5, +1); s6 Citadel Island, Baggage claim building, 2st scene (-102); s8 Citadel Island, Ticket office (-10); s14 Citadel Island, Shop (-10, -4, -5); s15 Citadel Island, Miss Bloop's Private Museum (-15); s22 Citadel Island, Downtown Pharmacy (-5); ... (26 more scenes)
- **Read in 32 scene(s):** s3, s6, s8, s14, s15, s22, s27, s36, s40, s45, s46, s48, s50, s57, ...

### 9 - Pisto-Laser

`inventory` - `FLAG_PISTOLASER` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** 1 when owned; the icon's 3D model is switched with STATE_INVENTORY: 1 = crystal, 2 = laser pistol (only model 2 is usable, PERSO.CPP). Quest stage lives in var 130. One decor (Citadel cube 7) is hidden when this is set.
- **Written by:** s95 Celebration Island, the only outside scene (icon state 2, found-object, =1); s148 Otringal, the Dissidents' hide out (icon state 2, icon state 1, found-object, =1)
- **Island decor:** CITADEL cube 7: 1 decor(s), body 41, hidden while set

### 10 - Emperor's sabre

`inventory` - `FLAG_SABRE` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** Found in the emperor's room on Island CX (s181, needs a little key).
- **Written by:** s181 Island CX, Room in the emperor (found-object, =1)
- **Read in 1 scene(s):** s181

### 11 - Wannie's glove

`inventory` - `FLAG_GANT` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** Reward from the injured Wannie at the ferryman landing (s97).
- **Written by:** s97 Wannies Island, the ferryman landing place scene (found-object, =1); s101 Wannies Island, the house of the firefly tart-giving family (found-object, =1); s112 Wannies Island, near the temple on the main (found-object, =1)
- **Read in 3 scene(s):** s97, s101, s112

### 12 - Proto-Pack

`inventory` - `FLAG_PROTOPACK` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** Taken in the baggage claim building (s5); STATE_INVENTORY(12,1) upgrades it to the super jet-pack when Baldino gives it (s138, var 117).
- **Written by:** s5 Citadel Island, Baggage claim building, 1st scene (found-object, =1); s138 Otringal, the Twinsen and Baldino crash site (icon state 1, found-object)
- **Read in 1 scene(s):** s5

### 13 - Ferry ticket

`inventory` - `FLAG_TICKET_FERRY` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** Bought at the Citadel (s8, 10 kashes) and Desert (s40, 12 kashes) ticket offices and at the Otringal souvenir shop (s147); consumed with SET_USED_INVENTORY when boarding (s43, s65). See 59.
- **Written by:** s8 Citadel Island, Ticket office (found-object, =1); s40 White Leaf Desert, Ticket office (found-object, =1); s43 Citadel Island, near the Harbor (marked used, =0); s65 White Leaf Desert, near Esmer Shuttle (marked used, =0); s147 Otringal, the Twinsunian souvenir shop (found-object, =1, marked used, =0)
- **Read in 5 scene(s):** s8, s40, s43, s65, s147

### 14 - Nitro-Meca-Penguins

`count` - `FLAG_MECA_PINGOUIN` - source: curated

- **Notes:** Quantity, max 10 (the debug cheat code adds 5; the "all objects" cheat sets 9). +1 from each penguin pickup; the engine decrements it per throw (OBJECT.CPP).
- **Written by:** s10 White Leaf Desert, Temple of Bú, 1st scene (found-object, =1); s11 White Leaf Desert, Temple of Bú, 2nd scene (found-object, +1); s12 White Leaf Desert, Esmer Base (found-object, +1); s14 Citadel Island, Shop (found-object, +1); s27 White Leaf Desert, School of Magic, 2nd scene (found-object, +1); s36 White Leaf Desert, Bazaar (found-object, +1); ... (8 more scenes)
- **Read in 14 scene(s):** s10, s11, s12, s14, s27, s36, s54, s59, s134, s147, s148, s181, s183, s192

### 15 - Can of GazoGem

`inventory` - `FLAG_GAZOGEM` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** Taken in the Francos gazogem factory (s143), handed to Baldino (s138, var 127).
- **Written by:** s138 Otringal, the Twinsen and Baldino crash site (=0); s143 Francos gazogem factory, 4th room (found-object, =1)
- **Read in 3 scene(s):** s89, s138, s143

### 16 - Dissidents' ring (half medallion)

`inventory` - `FLAG_DEMI_MEDAILLON` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** Given by the dissident in the emperor's palace room (s79, var 124 = 3); lost in the souvenir shop (s147).
- **Written by:** s79 Otringal, Emperor's palace 1st room (found-object, =1); s147 Otringal, the Twinsunian souvenir shop (=0)
- **Read in 2 scene(s):** s104, s147

### 17 - Gallic acid

`inventory` - `FLAG_ACIDE_GALLIQUE` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** Borrowed from Ker'aooc's helper (s35) or bought for 50 kashes at the Desert bazaar (s36); used on the neighbour's map (s9, var 54).
- **Written by:** s9 Citadel Island, Neighbor's house (=0); s35 White Leaf Desert, House of Ker'aooc the Healer (found-object, =1); s36 White Leaf Desert, Bazaar (found-object, =1)
- **Read in 5 scene(s):** s9, s35, s36, s49, s57

### 18 - Ferryman's song

`inventory` - `FLAG_CHANSON` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** Learned in the monk's house (s116, var 64). Read by the ferryman landings.
- **Written by:** s116 Wannies Island, the monk house (found-object, =1)
- **Read in 4 scene(s):** s97, s105, s116, s132

### 19 - Ring of lightning

`inventory` - `FLAG_ANNEAU_FOUDRE` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** Given by the Weather Wizard (s21). The engine also needs MagicLevel and MagicPoint to fire it (PERSO.CPP).
- **Written by:** s21 Citadel Island, Tent of the Weather Wizard (found-object, =1)
- **Read in 4 scene(s):** s21, s27, s34, s113

### 20 - Customer's umbrella

`inventory` - `FLAG_PARAPLUIE` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** Picked up in the sewers (s48) after the theft (var 57); handed over at Ker'aooc's (s35) or on the Citadel (s48); also for sale in the Otringal souvenir shop (s147).
- **Written by:** s35 White Leaf Desert, House of Ker'aooc the Healer (=0); s48 Citadel Island, downstairs the sewer grid (=0, found-object, =1); s147 Otringal, the Twinsunian souvenir shop (found-object, =1, marked used, =0)
- **Read in 3 scene(s):** s35, s48, s147

### 21 - Gems

`count` - `FLAG_GEMME` - source: curated

- **Notes:** Quantity, +1 per gem (s97, s100, s105, s111, s112, s129, s131, s132...), spent as ferry fares (-4 at s97/s105). Used as the currency of the mosquibee/wannie ferrymen.
- **Written by:** s97 Wannies Island, the ferryman landing place scene (marked used, -4, found-object, +1, +4); s100 Wannies Island, the mine, 2nd room (found-object, +1); s105 Mosquibees Island, the arrival scene (marked used, -4, found-object, +1); s111 Wannies Island, the mine, 3rd room (found-object, +1); s112 Wannies Island, near the temple on the main (found-object, +1); s129 Island Under Celebration, the hide out (found-object, +1); ... (3 more scenes)
- **Read in 3 scene(s):** s97, s105, s132

### 22 - Horn of the Blue Triton

`inventory` - `FLAG_CONQUE` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** Given by the rector for the Garden Balsam (s27, var 61 = 4). Owning it enables the horn behaviour (C_CONQUE, weapon key 4, PERSO.CPP); the horn keeps its own life refill (TabInv PtMagie), topped up by FULL_POINT and by leftover clover bonuses (GERELIFE.CPP, EXTRA.CPP).
- **Written by:** s27 White Leaf Desert, School of Magic, 2nd scene (found-object, =1)
- **Read in 2 scene(s):** s38, s49

### 23 - Blowgun

`inventory` - `FLAG_SARBACANE` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** Awarded after the blowgun test (s27, var 63 = 2); STATE_INVENTORY(23,1) makes it the blowtron in s128.
- **Written by:** s27 White Leaf Desert, School of Magic, 2nd scene (found-object, =1); s128 Mosquibees Island, Bee test room (icon state 1, found-object)
- **Read in 1 scene(s):** s27

### 24 - Memory viewer (ACF viewer)

`inventory` - `FLAG_DISQUE_ROUTE / FLAG_VISIONNEUSE` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** Picked up at Baldino's house (s38) or bought in the Otringal shop for 30 zlitos (s147). Opens the replay list of the movies flagged in vars 235-237 (PlayAllAcf). COMMON.H also names this index FLAG_DISQUE_ROUTE (itinerary disc).
- **Written by:** s38 White Leaf Desert, J. Baldino's House (=1, found-object); s147 Otringal, the Twinsunian souvenir shop (=1, found-object)
- **Read in 2 scene(s):** s38, s147

### 25 - Slice of firefly tart

`inventory` - `FLAG_TART_LUCI` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** Picked up in the tart-giving family's house (s101); handed over in the city (s118, var 64 = 2).
- **Written by:** s101 Wannies Island, the house of the firefly tart-giving family (found-object, =1); s118 Wannies Island, the city (marked used, =0)
- **Read in 1 scene(s):** s118

### 26 - Portable radio

`inventory` - `FLAG_RADIO` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** Given by Baldino at his house (s38, var 74 = 2).
- **Written by:** s17 Citadel Island, Sewer (below Downtown) (marked used); s38 White Leaf Desert, J. Baldino's House (found-object, =1); s47 Citadel Island, at the Sendell's sign (marked used); s60 White Leaf Desert, near J. Baldino's House (marked used); s87 Otringal, near Harbor (marked used)
- **Read in 6 scene(s):** s1, s17, s38, s47, s60, s87

### 27 - Garden Balsam

`inventory` - `FLAG_FLEUR` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** Picked up near the car jump (s62); handed to the rector (s27).
- **Written by:** s27 White Leaf Desert, School of Magic, 2nd scene (=0); s62 White Leaf Desert, near car jump (found-object, =1)
- **Read in 10 scene(s):** s27, s56, s57, s58, s61, s62, s63, s66, s67, s68

### 28 - Magic slate

`inventory` - `FLAG_ARDOISE` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** Awarded outside the Dome of the Slate (s44, var 61 = 5). MEMO_ARDOISE only stores plans while this is 1.
- **Written by:** s44 Citadel Island, outside the Dome of the Slate (found-object, =1)
- **Read in 3 scene(s):** s9, s31, s44

### 29 - Translator

`inventory` - `FLAG_TRADUCTEUR` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** Picked up in the Esmer shuttles (s41, s114, s115), Baldino's cell (s54), the Otringal prison (s84) and the control tower (s85). Shares its slot with the car part (7).
- **Written by:** s41 White Leaf Desert, Esmer Shuttle (found-object, =1, marked used); s54 Emerald Moon, Baldino's cell (found-object, =1, marked used); s84 Otringal Prison (found-object, =1, marked used); s85 Otringal, control tower entrance (found-object, =1, marked used); s114 White Leaf Desert, Esmer shuttle (found-object, =1, marked used); s115 Emerald Moon, Esmer shuttle (=1, found-object, marked used)
- **Read in 13 scene(s):** s12, s13, s23, s41, s54, s75, s77, s78, s84, s85, s92, s114, s115

### 30 - Wizard's diploma

`inventory` - `FLAG_DIPLOME` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** Awarded by the rector (s27, var 61 = 7); confiscated in the emperor's palace (s80); sold in the Otringal souvenir shop (s147).
- **Written by:** s27 White Leaf Desert, School of Magic, 2nd scene (found-object, =1); s80 Otringal, Emperor's palace last room (=0); s147 Otringal, the Twinsunian souvenir shop (found-object, =1, marked used, =0)
- **Read in 16 scene(s):** s8, s27, s38, s40, s49, s57, s62, s63, s65, s66, s67, s68, s70, s71, ...

### 31 - Key fragment of the Francos

`inventory` - `FLAG_DMKEY_KNARTA` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** Taken in the Francos village (s109). All four fragments together (31-34) set var 55 and STATE_INVENTORY turns them into the full key.
- **Written by:** s80 Otringal, Emperor's palace last room (icon state 1, found-object); s93 Celebration Island, inside the temple (=0); s103 Mosquibees Island, the scene on top of the mountain (icon state 1, found-object); s109 Francos Island, the village scene (=1, found-object, icon state 1); s117 Wannies Island, mine temple (icon state 1, found-object)
- **Read in 6 scene(s):** s80, s93, s103, s109, s117, s175
- **Island decor:** KNARTAS cube 3: 1 decor(s), body 90, visible only while set

### 32 - Key fragment of the Sups

`inventory` - `FLAG_DMKEY_SUP` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** Taken in the emperor's palace last room (s80, var 148 = 3).
- **Written by:** s80 Otringal, Emperor's palace last room (found-object, =1, =0); s103 Mosquibees Island, the scene on top of the mountain (=0); s109 Francos Island, the village scene (=0); s117 Wannies Island, mine temple (=0)
- **Read in 6 scene(s):** s88, s89, s93, s103, s109, s117

### 33 - Key fragment of the Mosquibees

`inventory` - `FLAG_DMKEY_MOSQUI` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** Taken on top of the mosquibee mountain (s103).
- **Written by:** s80 Otringal, Emperor's palace last room (=0); s103 Mosquibees Island, the scene on top of the mountain (found-object, =1, =0); s109 Francos Island, the village scene (=0); s117 Wannies Island, mine temple (=0)
- **Read in 5 scene(s):** s80, s93, s103, s109, s117

### 34 - Key fragment of the Wannies

`inventory` - `FLAG_DMKEY_BLAFARD` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** Taken in the wannies' mine temple (s117).
- **Written by:** s80 Otringal, Emperor's palace last room (=0); s103 Mosquibees Island, the scene on top of the mountain (=0); s109 Francos Island, the village scene (=0); s117 Wannies Island, mine temple (found-object, =1, =0)
- **Read in 9 scene(s):** s80, s93, s100, s103, s109, s111, s112, s117, s121

### 35 - Key to the queen's passage

`inventory` - `FLAG_CLE_REINE` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** Taken in the Building Company (s125); used at the throne (s104, var 48). Shares its slot with the flower (27).
- **Written by:** s104 Mosquibees Island, the Mosquibee Queen's throne (=0); s125 Wannies Island, Building Company (found-object, =1)
- **Read in 4 scene(s):** s97, s104, s106, s125

### 36 - Pick-axe

`inventory` - `FLAG_PIOCHE` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** Bought in the Francos shop (s171). Shares its slot with the gallic acid (17).
- **Written by:** s171 Francos Island, Shop (found-object, =1)
- **Read in 2 scene(s):** s95, s109

### 37 - Burgomaster's key

`inventory` - `FLAG_CLEF_BOURGMESTRE` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** Taken in the Francos village (s109), used in the burgomaster's house (s175).
- **Written by:** s109 Francos Island, the village scene (=1, found-object); s175 Francos Island, Burgermaster's house (=0)
- **Read in 2 scene(s):** s109, s175

### 38 - Burgomaster's notes

`inventory` - `FLAG_NOTE_BOURGMESTRE` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** Taken in the burgomaster's house (s175, var 147).
- **Written by:** s175 Francos Island, Burgermaster's house (=1, found-object)

### 39 - Protective spell

`inventory` - `FLAG_PROTECTION` - source: curated

- **Values:** `0` = not owned; `1` = owned
- **Notes:** Taken in the protection-spell cave (s184). Needs MagicPoint to cast (PERSO.CPP).
- **Written by:** s184 The protection spell cave, 2nd scene (=1, found-object)
- **Read in 2 scene(s):** s57, s184

## Scenario flags (40-195)

### 40 - Zoe position (track label)

`track` - `FLAG_SCAPHANDRE (unused)` - source: curated

- **Values:**
  - `0` = New game
  - `1` = Zoe talked: "Twinsen rush to Pharmacy"
  - `2` = Zoe outside door
  - `3` = Zoe walking to dinofly way point 1
  - `4` = Zoe walking to dinofly way point 2
  - `5` = Zoe walking to dinofly way point 3
  - `6` = Zoe walking to dinofly way point 4
  - `7` = Zoe walking to dinofly way point 5
  - `8` = Zoe walking to dinofly way point 6
  - `9` = Zoe walking to dinofly way point 7
  - `10` = Zoe walking to dinofly way point 8
  - `11` = Zoe walking to dinofly way point 9
  - `12` = Zoe walking to dinofly way point 10
  - `13` = Zoe walking to dinofly way point 11
  - `14` = Zoe walking to dinofly way point 12
  - `15` = Gave carpart to zoe and she mentions portable radio
  - `16` = Walking towards car
  - `17` = Walking towards car
  - `18` = Walking towards car
  - `19` = Walking towards car
  - `20` = Working on car
- **Notes:** COMMON.H calls index 40 FLAG_SCAPHANDRE ("the space suit is not in the inventory, so a scenario var"), but no code reads that constant; the shipped scripts use it as Zoe's position. Its value is a track label: TRACK_TO_VAR_GAME copies Zoe's current label in and VAR_GAME_TO_TRACK sends her to it, which is why the trainer's list reads like waypoints.
- **Written by:** s0 Citadel Island, Twinsen's house (=4, =1, =2, +1, saves track label); s1 Citadel Island, Twinsen's house Secret scene (=14, =20); s3 Citadel Island, Tavern (=14, =20); s7 Citadel Island, Mr. Paul House (=14, =20); s8 Citadel Island, Ticket office (=14, =20); s9 Citadel Island, Neighbor's house (+1, saves track label); ... (7 more scenes)
- **Read in 13 scene(s):** s0, s1, s3, s7, s8, s9, s14, s17, s42, s43, s46, s48, s49

### 41 - Hacienda women's steam room

`quest` - source: curated

- **Values:**
  - `0` = New/Reset: Grobo in first area
  - `1` = Grobo outside womens steam room: Twinsen entered steam room
  - `2` = Grobo attacks: Twinsen entered womens steam room a second time
- **Notes:** Written by the Grobo in the hacienda scenes (s24, s25, s30, s32, s72); cleared when you leave.
- **Written by:** s24 White Leaf Desert, Hacienda, 1st scene (=1); s25 White Leaf Desert, Turkish bath (women) (=1, =2, =0); s30 White Leaf Desert, Turkish bath (men) (=0); s32 White Leaf Desert, Secret passage in Turkish bath (mans) (=0); s72 White Leaf Desert, near Bald Mountain (=0)
- **Read in 6 scene(s):** s24, s25, s29, s30, s32, s72

### 42 - Dino-Fly injured status

`flag` - source: curated

- **Values:** `0` = Injured; `1` = Cured
- **Notes:** Set to 1 (cured) by the Dino-Fly in s49 when Twinsen brings the cure.
- **Written by:** s49 Citadel Island, near Dino-Fly (=1)
- **Read in 4 scene(s):** s0, s22, s38, s49

### 43 - Package centre

`quest` - source: curated

- **Values:**
  - `0` = New
  - `1` = Package in reception
  - `2` = Package centre: Pushed package onto final conveyor
  - `3` = Leaving package basement area
  - `4` = Grobo opens box
  - `5` = Paid guy to move box
- **Notes:** Package-centre conveyor / box state (s5, s6). 5 = paid the porter 102 kashes.
- **Written by:** s5 Citadel Island, Baggage claim building, 1st scene (=4, =3); s6 Citadel Island, Baggage claim building, 2st scene (=2, =0, =1, =5)
- **Read in 2 scene(s):** s5, s6

### 44 - Waiter in tavern cellar

`flag` - source: curated

- **Values:** `0` = Not in cellar; `1` = In cellar
- **Notes:** Toggled by the waiter in s3 and s4.
- **Written by:** s3 Citadel Island, Tavern (=0); s4 Citadel Island, Cellar of the Tavern (=0, =1)
- **Read in 1 scene(s):** s3

### 45 - Citadel shop keeper status

`quest` - source: curated

- **Values:**
  - `0` = Not scared away
  - `1` = Scared away
  - `2` = Returned
- **Notes:** Set to 1 by the shopkeeper (s14) and to 2 when he comes back (s42).
- **Written by:** s14 Citadel Island, Shop (=1); s42 Citadel Island, near Tavern (=2)
- **Read in 2 scene(s):** s14, s42

### 46 - Citadel shop cash register

`flag` - source: curated

- **Values:** `0` = Not stolen; `1` = Stole money
- **Written by:** s14 Citadel Island, Shop (=1)
- **Read in 1 scene(s):** s14

### 47 - Injured Wannie

`quest` - source: curated

- **Values:**
  - `0` = Wannie lying injured
  - `1` = Twinsen reached him ("Help me...")
  - `2` = Twinsen helped him and asked about the Queen
- **Notes:** Both writers are in the ferryman landing scene (s97).
- **Written by:** s97 Wannies Island, the ferryman landing place scene (=1, =2)
- **Read in 1 scene(s):** s97

### 48 - Throne door

`flag` - source: curated

- **Values:** `0` = Locked; `1` = Unlocked
- **Notes:** Set when Twinsen uses the queen's key (item 35) at the throne room door (s104).
- **Written by:** s104 Mosquibees Island, the Mosquibee Queen's throne (=1)
- **Read in 3 scene(s):** s97, s104, s125

### 49 - Package centre lower door

`flag` - source: curated

- **Values:** `0` = Locked; `1` = Unlocked
- **Notes:** Set when Twinsen uses a little key on the lower door (s6).
- **Written by:** s6 Citadel Island, Baggage claim building, 2st scene (=1)

### 50 - Package centre hidden money box

`quest` - source: curated

- **Values:**
  - `0` = not collected
  - `1` = collected (chest in the secret room, s16)
  - `2` = guard lets Twinsen through to his own parcel (s5)
- **Written by:** s5 Citadel Island, Baggage claim building, 1st scene (=2); s16 Citadel Island, Baggage claim building Secret scene, 3st scene (=1)
- **Read in 1 scene(s):** s16

### 51 - Stopping the rain

`quest` - source: curated

- **Values:**
  - `0` = New game
  - `1` = Speak to Weather Wizard
  - `2` = Spoke to Ralph
  - `3` = Killed Tralu and freed Ralph
  - `4` = Rain cleared and aliens landed
- **Notes:** The chapter-2 storm plot; tested by nearly every Citadel scene to decide whether the rain, Tralu's lighthouse and the aliens are active.
- **Written by:** s2 Citadel Island, Tralu 1st scene (=2, =3); s21 Citadel Island, Tent of the Weather Wizard (=1); s45 Citadel Island, near the tent of the Weather Wizard (=2); s46 Citadel Island, near Lighthouse (=4)
- **Read in 21 scene(s):** s1, s2, s3, s5, s6, s7, s8, s9, s14, s15, s17, s21, s22, s37, ...

### 52 - Citadel decor hidden by a flag nothing sets

`decor` - source: curated

- **Notes:** Only the island decor uses it: Citadel cube 7 (bodies 70, 71) and CITABAU cube 5 (body 35) are hidden when it is non-zero. No script writes it. The trainer's "Unknown".
- **Island decor:** CITABAU cube 5: 1 decor(s), body 35, hidden while set
- **Island decor:** CITADEL cube 7: 5 decor(s), body 70/71, hidden while set

### 53 - Tralu: first door / switch

`flag` - source: curated

- **Values:** `0` = closed; `1` = open
- **Also used by scripts, not in the list above:** 3, 4
- **Notes:** Set by actor 4 in Tralu's first scene (s2), toggled between tracks 100 and 101.
- **Written by:** s2 Citadel Island, Tralu 1st scene (=1, =0)
- **Read in 1 scene(s):** s2

### 54 - Pyramid key quest

`quest` - source: curated

- **Values:**
  - `0` = Not given gallic acid
  - `1` = Neighbour says he needs gallic acid but there's none left on the island
  - `2` = Gave gallic acid to old man
  - `3` = Got pyramid key from old man
  - `4` = Used pyramid key to open door
- **Written by:** s9 Citadel Island, Neighbor's house (=2, =1, =3); s17 Citadel Island, Sewer (below Downtown) (=4)
- **Read in 5 scene(s):** s9, s17, s35, s36, s49

### 55 - All four key fragments combined

`flag` - source: curated

- **Values:** `0` = no; `1` = combined
- **Notes:** Set by whichever island scene finishes the combination (s80, s103, s109, s117); read by the celebration temple (s93) and many islands. STATE_INVENTORY turns fragment slots 31-34 into the assembled key.
- **Written by:** s80 Otringal, Emperor's palace last room (=1); s103 Mosquibees Island, the scene on top of the mountain (=1); s109 Francos Island, the village scene (=1); s117 Wannies Island, mine temple (=1)
- **Read in 11 scene(s):** s88, s89, s93, s100, s103, s109, s111, s112, s117, s121, s175

### 56 - Lighthouse keeper / alien landing progress

`quest` - source: curated

- **Values:**
  - `0` = New
  - `3` = After lighthouse keeper runs away
  - `5` = After cut scene finishes and alien cut scene starts
- **Also used by scripts, not in the list above:** 1, 2, 4
- **Notes:** 1 is set by the hero in s2 when talking to the keeper (actor 2) while the storm plot is still open.
- **Written by:** s2 Citadel Island, Tralu 1st scene (=1, =3); s46 Citadel Island, near Lighthouse (=5)
- **Read in 9 scene(s):** s2, s21, s45, s46, s47, s48, s49, s50, s51

### 57 - Stolen umbrella

`quest` - source: curated

- **Values:**
  - `0` = Before umbrella stolen
  - `1` = Umbrella stolen
  - `2` = Retrieved Umbrella
  - `3` = Returned Umbrella on Citadel
- **Written by:** s22 Citadel Island, Downtown Pharmacy (=1); s48 Citadel Island, downstairs the sewer grid (=3, =2)
- **Read in 4 scene(s):** s22, s35, s48, s49

### 58 - Unused

`unused` - source: curated

- **Notes:** No script or engine references.

### 59 - Ferry ticket / crossing

`quest` - source: curated

- **Values:**
  - `0` = New
  - `1` = Purchased a ferry ticket
  - `3` = Boarded Ferry
  - `4` = Arrived at Desert island
  - `5` = Bought ferry ticket Desert Island
  - `7` = Entered ferry from Desert to Citadel
  - `8` = Arrived on Citadel Island
- **Notes:** 1 bought (Citadel office, s8), 5 bought (Desert office, s40), 3 boarded at the Citadel harbour (s43), 7 boarded at the Desert harbour (s65), 4 arrived Desert (s65), 8 arrived Citadel (s43).
- **Written by:** s8 Citadel Island, Ticket office (=1); s40 White Leaf Desert, Ticket office (=5); s43 Citadel Island, near the Harbor (=8, =3); s65 White Leaf Desert, near Esmer Shuttle (=4, =7)
- **Read in 4 scene(s):** s43, s59, s60, s65

### 60 - Tralu boss fight

`quest` - source: curated

- **Values:**
  - `0` = New
  - `1` = Entered Tralu Boss area
  - `2` = Defeated Tralu Boss
  - `3` = Unlocked door for lighthouse keeper
- **Notes:** 1 set on entering his zone (s2), 2 when Tralu's life reaches 0, 3 after the key opens the lighthouse-keeper's door.
- **Written by:** s2 Citadel Island, Tralu 1st scene (=3, =1, =2)
- **Read in 1 scene(s):** s2

### 61 - Becoming a wizard

`quest` - source: curated

- **Values:**
  - `0` = New
  - `3` = Paid wizard entrance fee
  - `4` = Received horn of blue triton (Gave rector flower)
  - `5` = received magic slate
  - `7` = Received wizard diploma
- **Also used by scripts, not in the list above:** 1, 2, 6
- **Notes:** 3 paid 120 kashes, 4 gave the flower, 5 got the slate (s44), 7 got the diploma (s27). 1, 2 and 6 are tested but never set.
- **Written by:** s27 White Leaf Desert, School of Magic, 2nd scene (=4, =3, =7); s44 Citadel Island, outside the Dome of the Slate (=5)
- **Read in 14 scene(s):** s8, s27, s28, s40, s44, s56, s57, s58, s61, s62, s63, s66, s67, s68

### 62 - Cure Dino-Fly

`quest` - source: curated

- **Values:**
  - `0` = New
  - `1` = Mr. Paul tells you to visit the wizard Ker'aooc - he knows how to cure dinofly
  - `2` = Ker'aooc house keeper says try school of magic
  - `3` = Arrived school of magic for first time. Rector appears
- **Notes:** Set from the pharmacy (s22), the harbour (s43), the Dino-Fly (s49), the sewers (s48), Ker'aooc's house (s35) and the school (s27).
- **Written by:** s22 Citadel Island, Downtown Pharmacy (=1); s27 White Leaf Desert, School of Magic, 2nd scene (=3); s35 White Leaf Desert, House of Ker'aooc the Healer (=2); s43 Citadel Island, near the Harbor (=1); s48 Citadel Island, downstairs the sewer grid (=1); s49 Citadel Island, near Dino-Fly (=1)
- **Read in 29 scene(s):** s8, s10, s21, s22, s24, s27, s29, s30, s35, s36, s38, s39, s40, s43, ...

### 63 - School of Magic: blowgun test

`quest` - source: curated

- **Values:**
  - `0` = New
  - `1` = Hit all targets
  - `2` = Received Blowgun
- **Notes:** 1 hit all targets (training room s33), 2 blowgun awarded (s27).
- **Written by:** s27 White Leaf Desert, School of Magic, 2nd scene (=2); s33 White Leaf Desert, School of Magic training room (=1)
- **Read in 3 scene(s):** s27, s33, s44

### 64 - Ferryman's song

`quest` - source: curated

- **Values:**
  - `0` = New
  - `1` = Fainting in firefly scene
  - `2` = Gave pie to old dude and got key
  - `3` = Unlocked door to chapel
  - `4` = Monk says to call on ferry man services to get to mousquibees
- **Notes:** 1 fainted after the tart (s101), 2 gave the tart and got the key (s118), 3 opened the chapel (s118), 4 monk says to call the ferryman (s121).
- **Written by:** s101 Wannies Island, the house of the firefly tart-giving family (=1); s118 Wannies Island, the city (=2, =3); s121 Wannies Island, the city temple (=4)
- **Read in 4 scene(s):** s101, s116, s118, s121

### 65 - School of magic entrance doors

`decor` - source: curated

- **Values:** `0` = Locked; `1` = Unlocked
- **Notes:** Also swaps decor in the desert island cube 9: bodies 54 hidden and 61-63 shown when set.
- **Written by:** s61 White Leaf Desert, at the School of Magic (=1)
- **Read in 1 scene(s):** s61
- **Island decor:** DESERT cube 9: 2 decor(s), body 54, hidden while set
- **Island decor:** DESERT cube 9: 6 decor(s), body 61/62/63, visible only while set

### 66 - Map in neighbour's house

`quest` - source: curated

- **Values:**
  - `0` = Unreadable
  - `1` = Readable
  - `2` = Read
- **Notes:** Written by the neighbour (s9): 1 map readable after the acid, 2 read.
- **Written by:** s9 Citadel Island, Neighbor's house (=2, =1)
- **Read in 1 scene(s):** s9

### 67 - Wannies island: hiding in a transport box

`quest` - `-` - source: curated

- **Values:**
  - `0` = not in box
  - `1` = inside the box
  - `2` = inside with the lid closed
- **Notes:** Renamed: the scenes are the wannies' warehouse and box building (s99, s124, s126), not Otringal.
- **Written by:** s99 Wannies Island, the scene near the entrance of the mine (=0); s124 Wannies Island, the box transport building (=0, =1, =2); s126 Wannies Island, Warehouse (=0)
- **Read in 1 scene(s):** s126

### 68 - Left Twinsen's house: Zoe cut scene

`flag` - source: curated

- **Values:** `0` = New; `1` = Left twinsen house - zoe cutscene: area 31
- **Notes:** Set by the Citadel Dino-Fly scene (s49) once Zoe has left.
- **Written by:** s49 Citadel Island, near Dino-Fly (=1)
- **Read in 1 scene(s):** s49

### 69 - Mosquibee rebellion progress

`quest` - source: curated

- **Values:**
  - `0` = New
  - `1` = Mousqibee cut scene "notification of soldiers attacking"
  - `2` = Left cut scene area and entered throne room
  - `3` = Ferry man to Island of the wannies cut scene
  - `4` = Mousqibee rebels say queen captured
- **Also used by scripts, not in the list above:** 5
- **Notes:** 2, 5 throne room (s104); 3 arrival (s105, needs gems); 1 blowtron test (s128); 4 hide-out (s129). Also toggles two decors in the Mosquibee cube 3.
- **Written by:** s104 Mosquibees Island, the Mosquibee Queen's throne (=2, =5); s105 Mosquibees Island, the arrival scene (=3); s128 Mosquibees Island, Bee test room (=1); s129 Island Under Celebration, the hide out (=4)
- **Read in 13 scene(s):** s96, s97, s99, s101, s104, s105, s106, s116, s118, s121, s126, s128, s129
- **Island decor:** MOSQUIBE cube 3: 1 decor(s), body 42, hidden while set
- **Island decor:** MOSQUIBE cube 3: 1 decor(s), body 47, visible only while set

### 70 - Alien cut scene after the rain

`flag` - source: curated

- **Values:** `0` = Not triggered; `1` = Cut scene over
- **Notes:** Set by the alien in s42.
- **Written by:** s42 Citadel Island, near Tavern (=1)
- **Read in 2 scene(s):** s42, s49

### 71 - Citadel taxi (Lupin-Bourg bike)

`quest` - source: curated

- **Values:**
  - `0` = not on the bike
  - `1` = on the bike
  - `2` = other destination chosen (s46, choice 415)
- **Notes:** Set by the taxi driver (actor 3 in s45, s46, s50 and actor 13 in s48) after the 8 kashes fare.
- **Written by:** s45 Citadel Island, near the tent of the Weather Wizard (=0, =1); s46 Citadel Island, near Lighthouse (=0, =2, =1); s48 Citadel Island, downstairs the sewer grid (=0, =1); s50 Citadel Island, at the Cliffs of the Woodbridge (=0, =1)
- **Read in 4 scene(s):** s45, s46, s48, s50

### 72 - Attacked children in the school

`quest` - source: curated

- **Values:**
  - `0` = New
  - `1` = Punched kid in red top and blue shorts
  - `2` = Punched grobo in school
  - `3` = Attacked sitting down or skipping child
  - `4` = Attacked blue shorts and grobo
  - `5` = Attacked blue shorts and skipping/sitting
  - `6` = Attacked grobo and skipping/sitting
  - `7` = Attacked all children
- **Notes:** Value = which of the three kids Twinsen hit (assigned per combination, 1-7).
- **Written by:** s37 Citadel Island, School (=3, =2, =6, =1, =5, =4, =7); s48 Citadel Island, downstairs the sewer grid (=0)
- **Read in 2 scene(s):** s37, s48

### 73 - Mosquibee blowtron

`flag` - source: curated

- **Values:** `0` = Don't own; `1` = Own
- **Notes:** Set when the blowtron is grabbed in the bee test room (s128).
- **Written by:** s128 Mosquibees Island, Bee test room (=1)
- **Read in 3 scene(s):** s101, s121, s125

### 74 - Fix the car / get the horn

`quest` - source: curated

- **Values:**
  - `0` = New game
  - `1` = Got car part
  - `2` = Gave car part to Zoe
  - `3` = Zoe called on portable radio
  - `4` = Zoe tells you to try the car at the race track
  - `5` = Traded Flower for Horn of Triton
- **Notes:** 1 got the car part (s38), 2 gave it to Zoe (s49), 3 Zoe calls (s60), 4 Zoe says try the race track (s65), 5 horn traded for the flower (s27).
- **Written by:** s27 White Leaf Desert, School of Magic, 2nd scene (=5); s38 White Leaf Desert, J. Baldino's House (=1); s49 Citadel Island, near Dino-Fly (=2); s60 White Leaf Desert, near J. Baldino's House (=3); s65 White Leaf Desert, near Esmer Shuttle (=4)
- **Read in 20 scene(s):** s38, s49, s55, s56, s57, s58, s59, s60, s61, s62, s63, s64, s65, s66, ...

### 75 - Unused

`unused` - source: curated

- **Notes:** No script or engine references.

### 76 - Door to fragment in the mine

`flag` - source: curated

- **Values:** `0` = Locked; `1` = Unlocked
- **Notes:** Set by a little key at the door (s112).
- **Written by:** s112 Wannies Island, near the temple on the main (=1)
- **Read in 1 scene(s):** s112

### 77 - Grobo brother waypoint

`quest` - source: curated

- **Values:**
  - `0` = New
  - `1` = At school
  - `2` = Citadel: Grobo brother way point
  - `3` = Citadel: Grobo brother way point?
  - `4` = Citadel: Grobo brother way point?
  - `5` = Normally vanishes at point 4
- **Written by:** s42 Citadel Island, near Tavern (=0); s48 Citadel Island, downstairs the sewer grid (=1, =2, =3, =4, =5)
- **Read in 2 scene(s):** s42, s48

### 78 - Grobo brother spawned

`flag` - source: curated

- **Values:** `0` = Not spawned; `1` = Spawned
- **Notes:** Cleared by five Citadel scenes on entry, set to 1 by the Grobo in s48.
- **Written by:** s5 Citadel Island, Baggage claim building, 1st scene (=0); s15 Citadel Island, Miss Bloop's Private Museum (=0); s22 Citadel Island, Downtown Pharmacy (=0); s37 Citadel Island, School (=0); s42 Citadel Island, near Tavern (=0); s48 Citadel Island, downstairs the sewer grid (=1)
- **Read in 5 scene(s):** s5, s15, s22, s37, s42

### 79 - Funfrock pizza scene

`quest` - source: curated

- **Values:**
  - `0` = Not triggered
  - `1` = Funfrock Speech
  - `2` = Funfrock Inside afterwards
- **Notes:** 1 set in the celebration temple (s93) when the combined key is used, 2 set at the track label 31 in s95.
- **Written by:** s93 Celebration Island, inside the temple (=1); s95 Celebration Island, the only outside scene (=2)
- **Read in 1 scene(s):** s95

### 80 - Wizard pedlar position (track label)

`track` - source: curated

- **Values:**
  - `0` = New
  - `2` = New
  - `5` = New
  - `6` = New
  - `7` = New
  - `9` = New
  - `11` = New
  - `14` = New
  - `16` = New
  - `17` = New
  - `19` = New
  - `20` = New
  - `22` = New
  - `23` = New
  - `26` = New
  - `27` = New
  - `29` = New
  - `30` = New
  - `33` = New
  - `34` = New
  - `36` = New
  - `37` = New
  - `39` = New
  - `40` = New
  - `43` = New
  - `44` = New
  - `46` = New
  - `49` = New
  - `51` = New
  - `52` = New
  - `54` = New
  - `56` = New
  - `58` = New
  - `59` = New
  - `61` = New
  - `64` = New
  - `66` = New
  - `69` = New
  - `70` = New
  - `72` = New
  - `74` = New
  - `77` = New
  - `78` = New
  - `80` = New
  - `81` = New
- **Notes:** The pedlar's track label, saved and restored across the desert scenes (TRACK_TO_VAR_GAME / VAR_GAME_TO_TRACK in 30 scenes). Tested against about 100 labels.
- **Written by:** s10 White Leaf Desert, Temple of Bú, 1st scene (saves track label); s11 White Leaf Desert, Temple of Bú, 2nd scene (saves track label); s24 White Leaf Desert, Hacienda, 1st scene (saves track label); s27 White Leaf Desert, School of Magic, 2nd scene (saves track label); s28 White Leaf Desert, School of Magic, 1st scene (saves track label); s29 White Leaf Desert, Hacienda, 2st scene (saves track label); ... (18 more scenes)
- **Read in 24 scene(s):** s10, s11, s24, s27, s28, s29, s33, s56, s57, s58, s60, s61, s62, s63, ...

### 81 - Wizard to Otringal

`quest` - source: curated

- **Values:**
  - `0` = New
  - `1` = Received Wizard diploma: Wizards disapeared
  - `2` = Accepted Sup invitation to go to Zeelich
- **Notes:** 1 diploma received and the wizards vanish (s27), 2 accepted Sup's invitation (s29).
- **Written by:** s27 White Leaf Desert, School of Magic, 2nd scene (=1); s29 White Leaf Desert, Hacienda, 2st scene (=2)
- **Read in 25 scene(s):** s10, s21, s24, s28, s29, s30, s35, s36, s38, s39, s40, s55, s56, s57, ...

### 82 - Ferryman taxi ride

`flag` - source: curated

- **Values:** `0` = Not on taxi; `1` = On taxi (cut scene not movie)
- **Notes:** 1 while the ferry cut scene runs (paying gems, s97, s105, s132).
- **Written by:** s97 Wannies Island, the ferryman landing place scene (=0, =1); s105 Mosquibees Island, the arrival scene (=0, =1); s132 Island Under Celebration, the arrival scene (=0, =1)
- **Read in 3 scene(s):** s97, s105, s132

### 83 - Main story sub-stage (wizard, Baldino, return to Citadel, Sendell)

`quest` - source: curated

- **Values:**
  - `0` = new
  - `1` = as a wizard going to the spaceship
  - `2` = leaving spaceship, attacked
  - `3` = attacked leaving spaceship
  - `4` = entered Otringal spaceport as a wizard (Joe scene)
  - `5` = escaped the cell, Joe gone
  - `6` = used the itinerary token to go home
  - `7` = landed on the Citadel: Zoe calls
  - `8` = Zoe says to go to the school of magic
  - `9` = Dino-Fly lands in the desert oasis
  - `10` = rector: lightning spell, get Sendell's ball, the clam
  - `11` = got the pearl of incandescence
  - `12` = used the lightning spell to get Sendell's ball
  - `13` = Baldino calls from the Emerald Moon
- **Also used by scripts, not in the list above:** 50
- **Notes:** Also drives the CITABAU decors of Citadel cubes 2, 5 and 7 (about 30 objects shown when set). The Otringal control tower scene (s85) temporarily adds 50 to this var as a marker and subtracts it again on the next load (SUB when the value is >= 50), so a saved value of stage + 50 is possible there.
- **Written by:** s0 Citadel Island, Twinsen's house (=8); s17 Citadel Island, Sewer (below Downtown) (=13); s27 White Leaf Desert, School of Magic, 2nd scene (=10); s29 White Leaf Desert, Hacienda, 2st scene (=1); s34 Citadel Island, Sendell's ball room (=12); s41 White Leaf Desert, Esmer Shuttle (=2, =6); ... (8 more scenes)
- **Read in 17 scene(s):** s0, s7, s12, s17, s27, s30, s32, s37, s41, s47, s49, s55, s84, s85, ...
- **Island decor:** CITABAU cube 2: 2 decor(s), body 18/19, visible only while set
- **Island decor:** CITABAU cube 5: 9 decor(s), body 55/56, visible only while set
- **Island decor:** CITABAU cube 7: 18 decor(s), body 55/56, visible only while set
- **Island decor:** CITABAU cube 7: 1 decor(s), body 101, hidden while set
- **Island decor:** DESERT cube 5: 3 decor(s), body 38/40/41, hidden while set

### 84 - Otringal prison: mosquibee cell

`quest` - source: curated

- **Values:**
  - `0` = cell locked
  - `1` = cell unlocked
  - `2` = mosquibee escaped
- **Written by:** s84 Otringal Prison (=2, =1, =0)
- **Read in 1 scene(s):** s84

### 85 - Emerald Moon / Baldino escape

`quest` - source: curated

- **Values:**
  - `3` = used token in the Desert shuttle
  - `4` = landed on the Emerald Moon
  - `5` = wrong password
  - `6` = right password
  - `7` = Baldino's cell unlocked
  - `8` = after the cut scene
  - `9` = after Baldino and the CX guy
  - `10` = unlocked the door to the spaceship
  - `11` = crash-landed with Baldino
- **Written by:** s13 Emerald Moon, Outside Baldino's cell(room #15) (=9); s23 Emerald Moon, next to outside Baldino's cell (=10); s54 Emerald Moon, Baldino's cell (=7, =8); s75 Emerald Moon, near circle entrance (=5, =4); s114 White Leaf Desert, Esmer shuttle (=3); s115 Emerald Moon, Esmer shuttle (=5, =6); ... (1 more scenes)
- **Read in 10 scene(s):** s13, s23, s31, s53, s54, s75, s77, s114, s115, s138

### 86 - Killed the green alien on the spaceship / guards triggered

`flag` - source: curated

- **Values:** `0` = Not triggered; `1` = Triggered
- **Written by:** s41 White Leaf Desert, Esmer Shuttle (=1); s92 Otringal, the space port scene (=1)
- **Read in 1 scene(s):** s92

### 87 - Otringal prison: Joe status

`flag` - source: curated

- **Values:** `0` = Joe there; `1` = Joe gone
- **Written by:** s83 Otringal, Prison entrance (=1, =0); s84 Otringal Prison (=0, =1); s90 Otringal, the Prison Exit scene (=0)
- **Read in 2 scene(s):** s83, s84

### 88 - Otringal prison: guards killed (bitmask)

`bits` - source: curated

- **Values:**
  - `0` = none
  - `1` = guard 1 (actor 2)
  - `2` = guard 2 (actor 13)
  - `4` = guard 3 (actor 14)
  - `8` = guard 4 (actor 21)
  - `15` = all
- **Notes:** Each bit is ADDed when the matching actor's life reaches 0 (s84). The trainer's list of values is a different observation of this same mask.
- **Written by:** s84 Otringal Prison (+1, +2, +4, +8)
- **Read in 1 scene(s):** s84

### 89 - On the moving platform in the Temple of Bu

`flag` - source: curated

- **Values:** `0` = not on it; `1` = on it
- **Notes:** Follows CARRY_BY / CARRY_OBJ_BY == 2 in s10 and s11.
- **Written by:** s10 White Leaf Desert, Temple of Bú, 1st scene (=1, =0); s11 White Leaf Desert, Temple of Bú, 2nd scene (=1, =0)
- **Read in 2 scene(s):** s10, s11

### 90 - Emerald Moon: entrance panel toggle

`flag` - source: curated

- **Values:** `0` = off; `1` = on
- **Notes:** Flips 0/1 each time zone 6 is triggered in the Emerald Moon entrance scenes (s13, s23, s52, s53, s167, s168).
- **Written by:** s13 Emerald Moon, Outside Baldino's cell(room #15) (=1, =0); s23 Emerald Moon, next to outside Baldino's cell (=1, =0); s52 Emerald Moon, entrance circle symbol building (=1, =0); s53 Emerald Moon, entrance X symbol building (=1, =0); s167 Emerald Moon, entrance triangle symbol room (=1, =0); s168 Emerald Moon, entrance square symbol room (=1, =0)
- **Read in 10 scene(s):** s13, s23, s31, s52, s53, s54, s75, s77, s167, s168

### 91 - Wannies mine: object on the plate

`flag` - source: curated

- **Values:** `0` = no; `1` = yes
- **Notes:** 1 while Twinsen carries the crate (actor 17 in s100, actor 7 in s122) inside the trigger zone.
- **Written by:** s100 Wannies Island, the mine, 2nd room (=0, =1); s122 Wannies Island, the mine, 1st room (=0, =1)
- **Read in 2 scene(s):** s100, s122

### 92 - Otringal control tower: people killed (bitmask)

`bits` - source: curated

- **Values:**
  - `0` = nobody
  - `1` = left scientist
  - `2` = right scientist
  - `4` = soldier
  - `8` = Sup
  - `15` = everybody
- **Notes:** ADDed in the control tower scene (s85) as each actor's life reaches 0.
- **Written by:** s85 Otringal, control tower entrance (=0, +1, +2, +4, +8)
- **Read in 1 scene(s):** s85

### 93 - Emerald Moon: yellow/black doors

`flag` - source: curated

- **Values:** `0` = Locked; `1` = Unlocked
- **Notes:** Set when the door switch is hit (s23, s54).
- **Written by:** s23 Emerald Moon, next to outside Baldino's cell (=1, =0); s54 Emerald Moon, Baldino's cell (=1, =0)
- **Read in 3 scene(s):** s13, s23, s54

### 94 - Dino-Fly location

`quest` - source: curated

- **Values:**
  - `1` = start (Zoe cut scene)
  - `2` = landed at the Dome of the Slate
  - `3` = landed on the Desert island
  - `4` = landed on the island across from the hacienda
  - `5` = at the Dome of the Slate (take-off)
  - `6` = Desert island from the Citadel
- **Notes:** Set by the Dino-Fly in s0, s44, s47, s49, s55, s73.
- **Written by:** s0 Citadel Island, Twinsen's house (=1); s44 Citadel Island, outside the Dome of the Slate (=2, =5, =6); s47 Citadel Island, at the Sendell's sign (=1); s49 Citadel Island, near Dino-Fly (=5, =6, =1); s55 White Leaf Desert, near Dino-fly (=3, =6, =5); s73 White Leaf Desert, at the island across from the Hacienda (=5, =6, =4)
- **Read in 4 scene(s):** s44, s49, s55, s73

### 95 - Read the wizard's note about finishing the spell

`flag` - source: curated

- **Values:** `0` = Not read; `1` = Read
- **Written by:** s21 Citadel Island, Tent of the Weather Wizard (=1)
- **Read in 1 scene(s):** s27

### 96 - Unused

`unused` - source: curated

- **Notes:** No script or engine references.

### 97 - Emerald Moon: circle door

`flag` - source: curated

- **Values:** `0` = Locked; `1` = Unlocked
- **Notes:** Set 1/0 by the diamond-building scene (s31); also swaps two door decors in the Emerald Moon cube 3.
- **Written by:** s31 Emerald Moon, entrance diamond shape building (=1, =0)
- **Read in 2 scene(s):** s31, s75
- **Island decor:** EMERAUDE cube 3: 1 decor(s), body 23, visible only while set
- **Island decor:** EMERAUDE cube 3: 1 decor(s), body 24, hidden while set

### 98 - Emerald Moon: triangle door

`flag` - source: curated

- **Values:** `0` = Locked; `1` = Unlocked
- **Notes:** Same scene and decor pair as 97.
- **Written by:** s31 Emerald Moon, entrance diamond shape building (=1, =0)
- **Read in 2 scene(s):** s31, s75
- **Island decor:** EMERAUDE cube 3: 1 decor(s), body 24, hidden while set
- **Island decor:** EMERAUDE cube 3: 1 decor(s), body 23, visible only while set

### 99 - Emerald Moon: square door

`flag` - source: curated

- **Values:** `0` = Locked; `1` = Unlocked
- **Notes:** Set in s31; decor pair in the Emerald Moon cube 4.
- **Written by:** s31 Emerald Moon, entrance diamond shape building (=1, =0)
- **Read in 2 scene(s):** s31, s77
- **Island decor:** EMERAUDE cube 4: 1 decor(s), body 23, visible only while set
- **Island decor:** EMERAUDE cube 4: 1 decor(s), body 24, hidden while set

### 100 - Emerald Moon: spaceship landed

`flag` - source: curated

- **Values:** `0` = Not landed; `1` = Landed
- **Notes:** Set in the shuttle scene (s115); shows two ship decors in the Emerald Moon cube 3.
- **Written by:** s115 Emerald Moon, Esmer shuttle (=1)
- **Island decor:** EMERAUDE cube 3: 2 decor(s), body 20/21, visible only while set

### 101 - Otringal fence panel 4 destroyed

`decor` - source: curated

- **Values:** `0` = Not destroyed; `1` = Destroyed
- **Notes:** Set in the prison exit scene (s90); swaps panel body 138 for the broken body 140 in Otringal cube 6.
- **Written by:** s90 Otringal, the Prison Exit scene (=1)
- **Read in 1 scene(s):** s90
- **Island decor:** OTRINGAL cube 6: 1 decor(s), body 138, hidden while set
- **Island decor:** OTRINGAL cube 6: 1 decor(s), body 140, visible only while set

### 102 - Otringal fence panel 2 destroyed

`decor` - source: curated

- **Values:** `0` = Not destroyed; `1` = Destroyed
- **Notes:** As 101.
- **Written by:** s90 Otringal, the Prison Exit scene (=1)
- **Read in 1 scene(s):** s90
- **Island decor:** OTRINGAL cube 6: 1 decor(s), body 138, hidden while set
- **Island decor:** OTRINGAL cube 6: 1 decor(s), body 140, visible only while set

### 103 - Otringal fence panel 1 destroyed

`decor` - source: curated

- **Values:** `0` = Not destroyed; `1` = Destroyed
- **Notes:** As 101.
- **Written by:** s90 Otringal, the Prison Exit scene (=1)
- **Read in 1 scene(s):** s90
- **Island decor:** OTRINGAL cube 6: 1 decor(s), body 138, hidden while set
- **Island decor:** OTRINGAL cube 6: 1 decor(s), body 140, visible only while set

### 104 - Otringal fence panel 5 destroyed

`decor` - source: curated

- **Values:** `0` = Not destroyed; `1` = Destroyed
- **Notes:** As 101.
- **Written by:** s90 Otringal, the Prison Exit scene (=1)
- **Read in 1 scene(s):** s90
- **Island decor:** OTRINGAL cube 6: 1 decor(s), body 138, hidden while set
- **Island decor:** OTRINGAL cube 6: 1 decor(s), body 140, visible only while set

### 105 - Otringal fence panel 6 destroyed

`decor` - source: curated

- **Values:** `0` = Not destroyed; `1` = Destroyed
- **Notes:** As 101.
- **Written by:** s90 Otringal, the Prison Exit scene (=1)
- **Read in 1 scene(s):** s90
- **Island decor:** OTRINGAL cube 6: 1 decor(s), body 138, hidden while set
- **Island decor:** OTRINGAL cube 6: 1 decor(s), body 140, visible only while set

### 106 - Otringal fence panel 3 destroyed

`decor` - source: curated

- **Values:** `0` = Not destroyed; `1` = Destroyed
- **Notes:** As 101.
- **Written by:** s90 Otringal, the Prison Exit scene (=1)
- **Read in 1 scene(s):** s90
- **Island decor:** OTRINGAL cube 6: 1 decor(s), body 138, hidden while set
- **Island decor:** OTRINGAL cube 6: 1 decor(s), body 140, visible only while set

### 107 - Undergas elevator

`quest` - source: curated

- **Values:**
  - `0` = Not taken elevator
  - `1` = Took elevator down
  - `2` = Taking right elevator up
  - `3` = Took left elevator back up
- **Notes:** Written in the elevator scenes (s120, s123); read by the scene next to it (s98).
- **Written by:** s120 The Elevator Platform Island, the only scene (=0); s123 Wannies Island, inside the elevator (=1, =3, =2)
- **Read in 3 scene(s):** s98, s120, s123

### 108 - Harbour boat animation state (Citadel and Desert)

`quest` - source: curated

- **Values:**
  - `0` = idle
  - `1-3` = boat positions
  - `4-6` = next positions
- **Also used by scripts, not in the list above:** 1, 2, 3, 4, 5, 6
- **Notes:** Renamed: it is only written by the harbour scenes (s43 and s60), which choose the next state at random (RND(2)) when their boat reaches the end of a track; not a single cut scene.
- **Written by:** s43 Citadel Island, near the Harbor (=4, =5, =6, =3, =2, =1); s60 White Leaf Desert, near J. Baldino's House (=4, =5, =6, =3, =2, =1)
- **Read in 3 scene(s):** s43, s60, s65

### 109 - Wizard's outfit

`flag` - source: curated

- **Values:** `0` = not owned; `1` = bought
- **Notes:** Bought for 50 kashes in the desert scenes (s62-s68); cleared in the men's bath (s30) when var 83 = 1. STATE_INVENTORY(4,1) makes the tunic the wizard's.
- **Written by:** s30 White Leaf Desert, Turkish bath (men) (=0); s62 White Leaf Desert, near car jump (=1); s63 White Leaf Desert, near Temple of Bù (=1); s65 White Leaf Desert, near Esmer Shuttle (=1); s66 White Leaf Desert, near Bald Mountain (=1); s67 White Leaf Desert, at the Temple of Bù (=1); ... (4 more scenes)
- **Read in 31 scene(s):** s0, s13, s15, s23, s27, s29, s38, s42, s43, s44, s45, s46, s47, s48, ...

### 110 - Otringal decor hidden outside chapters 6-8

`decor` - source: curated

- **Values:** `0` = decor shown; `1` = decor hidden
- **Notes:** 1 when CHAPTER < 6 or > 8 (s90, s138), 0 during chapters 6 to 8; hides four bodies (101-104) in Otringal cube 5.
- **Written by:** s90 Otringal, the Prison Exit scene (=1, =0); s138 Otringal, the Twinsen and Baldino crash site (=1, =0)
- **Island decor:** OTRINGAL cube 5: 4 decor(s), body 101/102/103/104, hidden while set

### 111 - Bu poster in the Citadel ticket office

`flag` - source: curated

- **Values:** `0` = Not read; `1` = Read
- **Written by:** s8 Citadel Island, Ticket office (=1)
- **Read in 1 scene(s):** s8

### 112 - Otringal: guards recognise Twinsen and attack

`flag` - source: curated

- **Values:** `0` = guards ignore Twinsen; `1` = guards attack
- **Notes:** Set by the Otringal harbour (s87, when var 124 = 1) and the celebration island guard (s95). Also toggles decors in Otringal cubes 3 and 4.
- **Written by:** s87 Otringal, near Harbor (=1); s95 Celebration Island, the only outside scene (=1)
- **Read in 11 scene(s):** s78, s82, s86, s87, s89, s90, s92, s95, s136, s139, s150
- **Island decor:** OTRINGAL cube 3: 3 decor(s), body 61/62, visible only while set
- **Island decor:** OTRINGAL cube 3: 2 decor(s), body 63, hidden while set
- **Island decor:** OTRINGAL cube 4: 2 decor(s), body 80/81, visible only while set

### 113 - Otringal casino: jackpot room state

`flag` - source: curated

- **Values:** `0` = no; `1` = jackpot hit
- **Notes:** Set 1 in the jackpot room (s133, VAR_CUBE(2) > 5), cleared in the casino entrance (s135) and lower city (s89).
- **Written by:** s89 Otringal, the Lower City scene (=0); s133 Otringal, Casino jackpot room (=1); s135 Otringal, Casino entrance (=0)
- **Read in 3 scene(s):** s89, s133, s135

### 114 - Unused

`unused` - source: curated

- **Notes:** No script or engine references.

### 115 - Otringal lowest elevators: which platform carries Twinsen

`track` - source: curated

- **Values:** `1-4` = carried by platform 3-6; `5` = none
- **Notes:** Written from zone 100 in s139, read by the elevator scene above it (s82).
- **Written by:** s139 Otringal, the lowest elevators (=1, =2, =3, =4, =5)
- **Read in 1 scene(s):** s82

### 116 - Otringal lift / safari giraffe position

`quest` - source: curated

- **Values:**
  - `0` = new
  - `1` = outside
  - `2` = inside
  - `3` = other stop
  - `4` = other stop
- **Notes:** Written by moving actors in s82, s89, s90, s139. Values 3 and 4 are missing from the trainer's list.
- **Written by:** s82 Otringal, the elevators to the Lower City (=3, =4, =2); s89 Otringal, the Lower City scene (=1, =2); s90 Otringal, the Prison Exit scene (=4, =3); s139 Otringal, the lowest elevators (=2, =1, =3)
- **Read in 2 scene(s):** s82, s139

### 117 - Baldino gave the super jet-pack

`flag` - source: curated

- **Values:** `0` = Not given; `1` = Given
- **Notes:** Set at the crash site (s138).
- **Written by:** s138 Otringal, the Twinsen and Baldino crash site (=1)
- **Read in 3 scene(s):** s87, s89, s138

### 118 - Desert island: healed the petanque player

`flag` - source: curated

- **Values:** `0` = Not healed; `1` = Healed
- **Notes:** Set by the player in s60.
- **Written by:** s60 White Leaf Desert, near J. Baldino's House (=1)
- **Read in 1 scene(s):** s60

### 119 - Zoe by the car: Citadel cube 9 decor

`flag` - source: curated

- **Values:** `0` = decor shown; `1` = decor hidden
- **Notes:** Set by the Dino-Fly scene (s49) once var 74 >= 3; hides the decor bodies 85 (CITADEL) and 114 (CITABAU) in cube 9.
- **Written by:** s49 Citadel Island, near Dino-Fly (=1)
- **Island decor:** CITABAU cube 9: 1 decor(s), body 114, hidden while set
- **Island decor:** CITADEL cube 9: 1 decor(s), body 85, hidden while set

### 120 - Francos gazogem factory: life container taken

`flag` - source: curated

- **Values:** `0` = in place; `1` = taken
- **Notes:** Set by the pickup in the first secret room (s145).
- **Written by:** s145 Francos gazogem factory, 1st secret room (=1)
- **Read in 1 scene(s):** s145

### 121 - Used the telescope on the hacienda roof

`flag` - source: curated

- **Values:** `0` = Not Used; `1` = Used
- **Written by:** s72 White Leaf Desert, near Bald Mountain (=1)
- **Read in 3 scene(s):** s44, s49, s55

### 122 - Leontine taxi

`flag` - source: curated

- **Values:** `0` = Not taking; `1` = Taking
- **Notes:** Set to 1 when boarding at the Otringal harbour (s87), reset on arrival at the celebration island (s95) and the Francos village (s109).
- **Written by:** s87 Otringal, near Harbor (=1); s95 Celebration Island, the only outside scene (=0); s109 Francos Island, the village scene (=0)
- **Read in 3 scene(s):** s87, s95, s109

### 123 - Taxi to the Francos island available

`flag` - source: curated

- **Values:** `0` = Can't take; `1` = Can take
- **Notes:** Set at the lower city (s89).
- **Written by:** s89 Otringal, the Lower City scene (=1)
- **Read in 3 scene(s):** s87, s89, s95

### 124 - Path to the Otringal rebels

`quest` - source: curated

- **Values:**
  - `0` = New game
  - `1` = Spoke to CX guy, yellow taxi spawns
  - `2` = Can take boat from Otringal
  - `3` = Talking to rick in bar about CX
  - `4` = Got ring of dissidents from Johnny rocket
  - `5` = Entered tourist shop rebel area
- **Written by:** s79 Otringal, Emperor's palace 1st room (=4); s87 Otringal, near Harbor (=2); s95 Celebration Island, the only outside scene (=1); s137 Otringal, Bar - Rick's room (=3); s148 Otringal, the Dissidents' hide out (=5)
- **Read in 13 scene(s):** s78, s79, s81, s82, s87, s89, s90, s95, s136, s137, s138, s139, s148

### 125 - Yellow taxi: Celebration island to Francos

`flag` - source: curated

- **Values:** `0` = New; `1` = yellow taxi to francos from celebration island cut scene
- **Written by:** s87 Otringal, near Harbor (=1, =0); s95 Celebration Island, the only outside scene (=1, =0); s109 Francos Island, the village scene (=1, =0)
- **Read in 3 scene(s):** s87, s95, s109

### 126 - Yellow taxi: Francos to Otringal

`flag` - source: curated

- **Values:** `0` = New; `1` = Taking yellow taxi from Francos to Otringal
- **Written by:** s87 Otringal, near Harbor (=1, =0); s95 Celebration Island, the only outside scene (=1, =0); s109 Francos Island, the village scene (=1, =0)
- **Read in 3 scene(s):** s87, s95, s109

### 127 - Gave the gazogem to Baldino

`flag` - source: curated

- **Values:** `0` = Not given; `1` = Given
- **Also used by scripts, not in the list above:** 2
- **Notes:** Set at the crash site (s138); the value 2 in the trainer's list is only tested, never set.
- **Written by:** s138 Otringal, the Twinsen and Baldino crash site (=1)
- **Read in 5 scene(s):** s48, s87, s89, s138, s143

### 128 - Mosquibee arrival scene: actor 10 reached track 11

`flag` - source: curated

- **Notes:** Written once (s105, plus demo s216); never read. Unknown purpose.
- **Written by:** s105 Mosquibees Island, the arrival scene (=1)

### 129 - Boarded the boat to the elevator

`flag` - source: curated

- **Values:** `0` = New; `1` = Boarded boat to elevator
- **Notes:** Toggled in the Francos platform scene (s107) and elevator island (s120); two decors in KNARTAS cube 1 and ASCENCE cube 1 are hidden when set.
- **Written by:** s107 Francos Island, the platform scene (=0, =1); s120 The Elevator Platform Island, the only scene (=1, =0)
- **Read in 2 scene(s):** s107, s120
- **Island decor:** ASCENCE cube 1: 2 decor(s), body 31/35, hidden while set
- **Island decor:** KNARTAS cube 1: 1 decor(s), body 43, hidden while set

### 130 - Laser pistol quest

`quest` - source: curated

- **Values:**
  - `0` = Have nothing
  - `1` = Have crystal
  - `2` = Broken pistol
  - `3` = Working pistol
- **Notes:** 1 has the crystal (s95), 2 broken pistol (s148), 3 working pistol (s95). Matches inventory slot 9's model states.
- **Written by:** s95 Celebration Island, the only outside scene (=1, =3); s148 Otringal, the Dissidents' hide out (=3, =2)
- **Read in 2 scene(s):** s95, s148

### 131 - Otringal bar show

`quest` - source: curated

- **Values:**
  - `0` = kiss on stage
  - `1` = singer about to come on stage
  - `2` = singer on stage
  - `3` = kiss about to come on stage
- **Notes:** Written by the bar actors (s134) and by the harbour/bar entrance (s87, s136), which reset it with RND(2).
- **Written by:** s87 Otringal, near Harbor (=0, =2); s134 Otringal, Bar backstage (=0, =1, =2, =3); s136 Otringal, Bar entrance (=0, =2)
- **Read in 2 scene(s):** s134, s136

### 132 - Johnny Rocket position

`flag` - source: curated

- **Values:** `0` = by the swimming pool; `1` = in his room
- **Written by:** s81 Otringal, the Imperial Hotel (=1)
- **Read in 1 scene(s):** s81

### 133 - Otringal decor hidden before chapter 6

`decor` - source: curated

- **Values:** `0` = decor shown; `1` = decor hidden
- **Notes:** 1 when CHAPTER < 6 (s90, s138); hides body 105 in Otringal cube 5. Renamed: the trainer's "left cell block" is only an observation.
- **Written by:** s90 Otringal, the Prison Exit scene (=1, =0); s138 Otringal, the Twinsen and Baldino crash site (=1, =0)
- **Island decor:** OTRINGAL cube 5: 1 decor(s), body 105, hidden while set

### 134 - Wannies mine, 2nd room: gems collected (bitmask 1, 2, 4)

`bits` - source: curated

- **Values:**
  - `0` = none
  - `1` = gem A
  - `2` = gem B
  - `4` = gem C
  - `7` = all three
- **Notes:** ADDs 1, 2 or 4 as each gem is taken (s100). Reset to 0 when the ferryman is paid in gems (s97).
- **Written by:** s97 Wannies Island, the ferryman landing place scene (=0); s100 Wannies Island, the mine, 2nd room (+1, +2, +4)
- **Read in 1 scene(s):** s100

### 135 - Wannies mine, 3rd room: gem

`flag` - source: curated

- **Values:** `0` = in place; `1` = taken
- **Notes:** Set in s111; reset with the other gem flags at the ferryman (s97).
- **Written by:** s97 Wannies Island, the ferryman landing place scene (=0); s111 Wannies Island, the mine, 3rd room (=1)
- **Read in 1 scene(s):** s111

### 136 - Wannies island near the temple: gems collected (bitmask 1, 2)

`bits` - source: curated

- **Values:**
  - `0` = none
  - `1` = gem A
  - `2` = gem B
  - `3` = both
- **Notes:** s112; reset at the ferryman (s97).
- **Written by:** s97 Wannies Island, the ferryman landing place scene (=0); s112 Wannies Island, near the temple on the main (+1, +2)
- **Read in 1 scene(s):** s112

### 137 - Mosquibee island arrival: gems (bitmask 1, 2)

`bits` - source: curated

- **Values:**
  - `0` = none
  - `1` = gem A
  - `2` = gem B
  - `3` = both
- **Notes:** s105.
- **Written by:** s105 Mosquibees Island, the arrival scene (=0, +1, +2)
- **Read in 1 scene(s):** s105

### 138 - Mosquibee island arrival: gems near the bridge (bitmask 1, 2)

`bits` - source: curated

- **Values:**
  - `0` = none
  - `1` = gem A
  - `2` = gem B
  - `3` = both
- **Notes:** s105.
- **Written by:** s105 Mosquibees Island, the arrival scene (=0, +1, +2)
- **Read in 1 scene(s):** s105

### 139 - Mosquibee gem in the spider area

`flag` - source: curated

- **Values:** `0` = in place; `1` = taken
- **Notes:** Set in the passage to the fragment (s149); reset in s105.
- **Written by:** s105 Mosquibees Island, the arrival scene (=0); s149 Mosquibees Island, the passage to the fragment (=1)
- **Read in 1 scene(s):** s149

### 140 - Island under Celebration arrival: gems (bitmask 1, 2)

`bits` - source: curated

- **Values:**
  - `0` = none
  - `1` = gem A
  - `2` = gem B
  - `3` = both
- **Notes:** s132.
- **Written by:** s132 Island Under Celebration, the arrival scene (=0, +1, +2)
- **Read in 1 scene(s):** s132

### 141 - Island under Celebration upper scene: gems (bitmask 1, 2, 4)

`bits` - source: curated

- **Values:**
  - `0` = none
  - `1` = gem A
  - `2` = gem B
  - `4` = gem C
  - `7` = all three
- **Notes:** s131.
- **Written by:** s131 Island Under Celebration, the upper scene (+1, +2, +4); s132 Island Under Celebration, the arrival scene (=0)
- **Read in 1 scene(s):** s131

### 142 - Island under Celebration hide-out gem

`flag` - source: curated

- **Values:** `0` = in place; `1` = taken
- **Notes:** s129.
- **Written by:** s129 Island Under Celebration, the hide out (=1); s132 Island Under Celebration, the arrival scene (=0)
- **Read in 1 scene(s):** s129

### 143 - Carried by the turtle

`flag` - source: curated

- **Values:** `0` = no; `1` = riding the turtle
- **Notes:** 1 while CARRY_BY == 3 (s64) or 2 (s113).
- **Written by:** s64 White Leaf Desert, at the Bell (=1, =0); s113 White Leaf Desert, Pearl of Incandescence room (=1, =0)
- **Read in 2 scene(s):** s64, s113

### 144 - Otringal souvenir shop: wizard diploma sold

`flag` - source: curated

- **Values:** `0` = on sale; `1` = sold
- **Written by:** s147 Otringal, the Twinsunian souvenir shop (=0, =1)
- **Read in 1 scene(s):** s147

### 145 - Otringal souvenir shop: ferry ticket sold

`flag` - source: curated

- **Values:** `0` = on sale; `1` = sold
- **Written by:** s147 Otringal, the Twinsunian souvenir shop (=0, =1)
- **Read in 1 scene(s):** s147

### 146 - Otringal souvenir shop: umbrella sold

`flag` - source: curated

- **Values:** `0` = on sale; `1` = sold
- **Written by:** s147 Otringal, the Twinsunian souvenir shop (=0, =1)
- **Read in 1 scene(s):** s147

### 147 - Burgomaster's notes found

`flag` - source: curated

- **Values:** `0` = Not found; `1` = Found
- **Notes:** Set in the burgomaster's house (s175); read by the village (s109).
- **Written by:** s175 Francos Island, Burgermaster's house (=1)
- **Read in 2 scene(s):** s109, s175

### 148 - Sup fragment / emperor's palace

`quest` - source: curated

- **Values:**
  - `0` = New
  - `3` = Got sups key fragment
  - `4` = Broke window above Sup fragment
- **Also used by scripts, not in the list above:** 2
- **Notes:** 3 got the Sup fragment (s80), 4 broke the window above it; tested 2 to 4 in the palace rooms s151-s166.
- **Written by:** s80 Otringal, Emperor's palace last room (=3, =4)
- **Read in 15 scene(s):** s80, s151, s152, s153, s154, s155, s156, s158, s159, s160, s162, s163, s164, s165, ...

### 149 - Desert island: extra props from chapter 4

`decor` - source: curated

- **Values:** `0` = props hidden; `1` = props shown
- **Notes:** Set to 1 by about 60 desert actors (bodies 15-17) when CHAPTER == 4; shown when set in desert cubes 4, 8, 9 and 10 (24 decors). Renamed: not "Landed with Dino-Fly".
- **Written by:** s55 White Leaf Desert, near Dino-fly (=1); s56 White Leaf Desert, near the Camel (=1); s61 White Leaf Desert, at the School of Magic (=1); s66 White Leaf Desert, near Bald Mountain (=1)
- **Island decor:** DESERT cube 4: 7 decor(s), body 15/16/17, visible only while set
- **Island decor:** DESERT cube 8: 4 decor(s), body 15/16/17, visible only while set
- **Island decor:** DESERT cube 9: 5 decor(s), body 15/16/17, visible only while set
- **Island decor:** DESERT cube 10: 8 decor(s), body 15/16/17, visible only while set

### 150 - Mosquibee island: Twinsen climbing the rope

`flag` - source: curated

- **Values:** `0` = New; `1` = Twinsen climbing rope to mosquibee cutscene
- **Notes:** 1 during the cut scene (s105), cleared in the maze (s106).
- **Written by:** s105 Mosquibees Island, the arrival scene (=1, =0); s106 Mosquibees Island, the maze (=0)
- **Read in 1 scene(s):** s106

### 151 - Francos arrival flag

`flag` - source: curated

- **Values:** `0` = not set; `1` = set on arrival
- **Notes:** Set by the Francos arrival scene (s110), cleared by the mosquibee passage to CX (s176). Unknown purpose.
- **Written by:** s110 Francos Island, the arrival scene (=1); s176 Mosquibees Island, the passage to Island CX (=0)
- **Read in 1 scene(s):** s176

### 152 - Emperor's shuttle

`quest` - source: curated

- **Values:**
  - `0` = new
  - `1` = taking the shuttle to the final fragment (s182)
  - `2` = landed at the palace (s189)
- **Written by:** s182 Island CX, Esmer Shuttle (=1); s189 Otringal, Esmer shuttle (=2)
- **Read in 4 scene(s):** s88, s110, s177, s189

### 153 - Told the grand rector about the pearl and Sendell's ball

`flag` - source: curated

- **Values:**
  - `0` = New
  - `1` = Spoke to Grand rector about pearl and informed of sendells ball and informed of location
- **Notes:** All three of 153-155 are set by the rector in the school (s27).
- **Written by:** s27 White Leaf Desert, School of Magic, 2nd scene (=1)
- **Read in 1 scene(s):** s27

### 154 - Asked where the pearl was and was told: in the clam

`flag` - source: curated

- **Values:** `0` = New; `1` = Asked grand rector where pearl was and told it's in clam
- **Written by:** s27 White Leaf Desert, School of Magic, 2nd scene (=1)
- **Read in 1 scene(s):** s27

### 155 - Told the rector about the pearl of incandescence

`flag` - source: curated

- **Values:** `0` = New
- **Also used by scripts, not in the list above:** 1
- **Notes:** Set when Twinsen brings the pearl to the rector (s27, track 211).
- **Written by:** s27 White Leaf Desert, School of Magic, 2nd scene (=1)
- **Read in 1 scene(s):** s27

### 156 - Otringal upper city: emperor's shuttle decor

`decor` - source: curated

- **Values:** `0` = decor hidden; `1` = shuttle decor shown
- **Notes:** 1 once var 152 >= 2 (s88); shows body 31 in Otringal cube 1. Renamed from "Left emperor shuttle".
- **Written by:** s88 Otringal, the Upper City scene (=0, =1)
- **Island decor:** OTRINGAL cube 1: 1 decor(s), body 31, visible only while set

### 157 - The End: Dark Monk destroyed

`quest` - `PLAY_THE_END` - source: curated

- **Values:**
  - `0` = game running
  - `1` = ending: the Dark Monk is dead (s188)
  - `2` = set in the sewer scene (s48), Twinsen/Dino-Fly cut scene
- **Notes:** COMMON.H: PLAY_THE_END = (ListVarGame[157] > 0). While set the engine keeps the ending music (LM_PLAY_MUSIC), skips voice transfers in the desert (MESSAGE.CPP), blocks saving (SAVEGAME.CPP) and 57 CITABAU decors in cube 5 appear.
- **Written by:** s48 Citadel Island, downstairs the sewer grid (=2); s188 The Dark Monk statue, 4th scene (last) (=1)
- **Read in 3 scene(s):** s46, s48, s60
- **Island decor:** CITABAU cube 5: 57 decor(s), body 57/58/59/60/61/62/63/65/66/67/68/69/70/71/72/73/74/75/76/77, visible only while set

### 158 - Looked at the alien spaceship (cut scene)

`flag` - source: curated

- **Values:** `0` = New; `1` = Peered into spaceship on desert island and saw cut scene
- **Notes:** Set in s42 (Citadel) and s65 (Desert) when Twinsen uses the viewing zone.
- **Written by:** s42 Citadel Island, near Tavern (=0, =1); s65 White Leaf Desert, near Esmer Shuttle (=0, =1)
- **Read in 3 scene(s):** s42, s65, s114

### 159 - Funfrock button and the children

`quest` - source: curated

- **Values:**
  - `1` = Funfrock pushed the button (s186)
  - `2` = children fall through the first scene (s185)
  - `3` = children fall through scene 3 and Funfrock teleports (s187)
- **Also used by scripts, not in the list above:** 0
- **Written by:** s185 The Dark Monk statue, the wizards, 1st scene (=2); s186 The Dark Monk statue, the top, 2nd scene (=1); s187 The Dark Monk statue, 3rd scene (=3)
- **Read in 3 scene(s):** s185, s186, s187

### 160 - Emperor triggered the Moon

`flag` - source: curated

- **Values:** `0` = no; `1` = triggered (s181)
- **Notes:** GameOver() plays the DELUGE cinematic instead of the normal game over when this is > 0 (GAMEMENU.CPP).
- **Written by:** s181 Island CX, Room in the emperor (=1)
- **Read in 10 scene(s):** s80, s87, s93, s99, s108, s120, s123, s150, s181, s182

### 161 - Joe's shell

`quest` - source: curated

- **Values:** `1` = conversation in the Tralu dungeon (s20); `2` = freed from the shell (s61)
- **Also used by scripts, not in the list above:** 0
- **Written by:** s20 Citadel Island, Tralu 3rd scene (=1); s61 White Leaf Desert, at the School of Magic (=2)
- **Read in 2 scene(s):** s20, s61

### 162 - Dark Monk statue 1st scene: teleporters destroyed (bitmask)

`bits` - source: curated

- **Values:**
  - `1` = teleporter 1 (actor 45)
  - `2` = teleporter 2 (actor 46)
  - `3` = both
- **Notes:** ADDed when the statue boss's life reaches 0 (s185).
- **Written by:** s185 The Dark Monk statue, the wizards, 1st scene (+1, +2)
- **Read in 1 scene(s):** s185

### 163 - Dark Monk statue: top teleporter destroyed

`flag` - source: curated

- **Values:** `0` = Not destroyed; `1` = Destroyed
- **Notes:** s186.
- **Written by:** s186 The Dark Monk statue, the top, 2nd scene (=1)
- **Read in 1 scene(s):** s186

### 164 - Citadel sewer cover A (cube 5)

`decor` - source: curated

- **Values:** `0` = hidden; `1` = shown
- **Notes:** Set 1 by s42, s47, s49, cleared by s48; shows body 47 (CITADEL) or 64 (CITABAU) in cube 5. The demo build force-sets 164 and 165 to hide the sewer covers (EXTFUNC.CPP).
- **Written by:** s42 Citadel Island, near Tavern (=1); s47 Citadel Island, at the Sendell's sign (=1); s48 Citadel Island, downstairs the sewer grid (=0); s49 Citadel Island, near Dino-Fly (=1); s94 Empty water outside scene (=1)
- **Island decor:** CITABAU cube 5: 1 decor(s), body 64, visible only while set
- **Island decor:** CITADEL cube 5: 1 decor(s), body 47, visible only while set

### 165 - Citadel sewer cover B (cube 7)

`decor` - source: curated

- **Values:** `0` = hidden; `1` = shown
- **Notes:** Set 1 by s48 and s49, cleared by s42; shows body 47 / 64 in cube 7.
- **Written by:** s42 Citadel Island, near Tavern (=0); s48 Citadel Island, downstairs the sewer grid (=1); s49 Citadel Island, near Dino-Fly (=1); s94 Empty water outside scene (=1)
- **Island decor:** CITABAU cube 7: 1 decor(s), body 64, visible only while set
- **Island decor:** CITADEL cube 7: 1 decor(s), body 47, visible only while set

### 166 - Citadel cube 6: alien prop during chapter 2

`decor` - source: curated

- **Values:** `0` = hidden; `1` = shown
- **Notes:** 1 when CHAPTER == 2 in s42, s43 and s46; shows body 56 (CITADEL) or 86 (CITABAU) in cube 6.
- **Written by:** s42 Citadel Island, near Tavern (=1, =0); s43 Citadel Island, near the Harbor (=0, =1); s46 Citadel Island, near Lighthouse (=1, =0); s47 Citadel Island, at the Sendell's sign (=0); s94 Empty water outside scene (=1, =0)
- **Island decor:** CITABAU cube 6: 1 decor(s), body 86, visible only while set
- **Island decor:** CITADEL cube 6: 1 decor(s), body 56, visible only while set

### 167 - Desert cube 6: harbour ferry prop

`decor` - source: curated

- **Values:** `0` = hidden; `1` = shown
- **Notes:** 0 while the ferry ride runs (var 59 = 3) or in chapter 4, 1 otherwise (s47-s71); shows body 48 in desert cube 6.
- **Written by:** s47 Citadel Island, at the Sendell's sign (=0); s59 White Leaf Desert, near Lighthouse (=0, =1); s60 White Leaf Desert, near J. Baldino's House (=1, =0); s64 White Leaf Desert, at the Bell (=1, =0); s65 White Leaf Desert, near Esmer Shuttle (=0, =1); s66 White Leaf Desert, near Bald Mountain (=1, =0); ... (3 more scenes)
- **Island decor:** DESERT cube 6: 1 decor(s), body 48, visible only while set

### 168 - Desert: tour / car-jump gate

`flag` - source: curated

- **Values:** `0` = off; `1` = on
- **Notes:** Set by the car-jump actor in s62 (zone 5), cleared when bought back at the tour (s57, 40 zlitos). Unknown purpose.
- **Written by:** s57 White Leaf Desert, at the Von Kournil tour (=0); s62 White Leaf Desert, near car jump (=1, =0)
- **Read in 1 scene(s):** s57

### 169 - Pearl of Incandescence found

`flag` - source: curated

- **Values:** `0` = no; `1` = cave found (s113)
- **Notes:** Cleared when the pearl is handed to the Weather Wizard (s21).
- **Written by:** s21 Citadel Island, Tent of the Weather Wizard (=0); s113 White Leaf Desert, Pearl of Incandescence room (=1)
- **Read in 3 scene(s):** s21, s27, s113

### 170 - Itinerary token held (Esmer shuttle ticket)

`flag` - source: curated

- **Values:** `0` = no; `1` = holding it
- **Notes:** Set with inventory slot 5's token model (s12, s85, s177), cleared by the Esmer shuttles (s41, s114, s182).
- **Written by:** s12 White Leaf Desert, Esmer Base (=1); s41 White Leaf Desert, Esmer Shuttle (=0); s85 Otringal, control tower entrance (=1); s114 White Leaf Desert, Esmer shuttle (=0); s177 Island CX, Stairs in the control tower (=1); s182 Island CX, Esmer Shuttle (=0)
- **Read in 6 scene(s):** s12, s41, s85, s114, s177, s182

### 171 - As a wizard, alien asks to visit their planet

`flag` - source: curated

- **Values:** `0` = New; `1` = As wizard alien asking to visit their planet
- **Notes:** Set at the hacienda (s29), read at the school (s27).
- **Written by:** s29 White Leaf Desert, Hacienda, 2st scene (=1)
- **Read in 1 scene(s):** s27

### 172 - Read only by two desert actors

`flag` - source: curated

- **Notes:** Read by actors 7 and 8 of s62 (together with var 27) but never written by any script. Unknown.
- **Read in 1 scene(s):** s62

### 173 - Citadel cube 3 decor

`decor` - source: curated

- **Values:** `0` = hidden; `1` = shown
- **Notes:** 1 in s47, 0 in s50; shows body 20 (CITADEL) or 28 (CITABAU) in cube 3.
- **Written by:** s47 Citadel Island, at the Sendell's sign (=1); s50 Citadel Island, at the Cliffs of the Woodbridge (=0)
- **Island decor:** CITABAU cube 3: 1 decor(s), body 28, visible only while set
- **Island decor:** CITADEL cube 3: 1 decor(s), body 20, visible only while set

### 174 - Attacked the guard in the Otringal tower on returning to the Citadel

`flag` - source: curated

- **Values:** `0` = New; `1` = Attacked guard in Otringal tower when returning to citadel
- **Notes:** 1 when the guard is hit (s85).
- **Written by:** s85 Otringal, control tower entrance (=0, =1)
- **Read in 1 scene(s):** s85

### 175 - Temple of Bu puzzle stage

`quest` - source: curated

- **Values:**
  - `0` = reset
  - `1` = set
  - `2 and 3` = other steps
- **Also used by scripts, not in the list above:** 2, 3
- **Notes:** Set by the temple actors (s10, s11).
- **Written by:** s10 White Leaf Desert, Temple of Bú, 1st scene (=0, =1, =2, =3); s11 White Leaf Desert, Temple of Bú, 2nd scene (=3)
- **Read in 2 scene(s):** s10, s11

### 176 - Bu Meca-Penguin taken from the statue plinth

`flag` - source: curated

- **Values:** `0` = Not collected; `1` = Collected
- **Notes:** s11.
- **Written by:** s11 White Leaf Desert, Temple of Bú, 2nd scene (=1)
- **Read in 1 scene(s):** s11

### 177 - Dark Monk statue 3rd scene: switch 1

`flag` - source: curated

- **Values:** `0` = not triggered; `1` = triggered
- **Notes:** Set in s187 (all of 177-181); each is tested > 0 there.
- **Written by:** s187 The Dark Monk statue, 3rd scene (=1)
- **Read in 1 scene(s):** s187

### 178 - Dark Monk statue 3rd scene: switch 2

`flag` - source: curated

- **Values:** `0` = not triggered; `1` = triggered
- **Written by:** s187 The Dark Monk statue, 3rd scene (=1)
- **Read in 1 scene(s):** s187

### 179 - Dark Monk statue 3rd scene: switch 3

`flag` - source: curated

- **Values:** `0` = not triggered; `1` = triggered
- **Written by:** s187 The Dark Monk statue, 3rd scene (=1)
- **Read in 1 scene(s):** s187

### 180 - Dark Monk statue 3rd scene: teleporter destroyed

`flag` - source: curated

- **Values:** `0` = not triggered; `1` = triggered
- **Written by:** s187 The Dark Monk statue, 3rd scene (=1)
- **Read in 1 scene(s):** s187

### 181 - Dark Monk statue 3rd scene: switch 5

`flag` - source: curated

- **Values:** `0` = not triggered; `1` = triggered
- **Written by:** s187 The Dark Monk statue, 3rd scene (=1)
- **Read in 1 scene(s):** s187

### 182 - Desert cube 4: coast decor

`decor` - source: curated

- **Values:** `0` = hidden; `1` = shown
- **Notes:** Set to 1 by the desert scenes (s56, s59-s61) and 0 in s55; shows body 1 in desert cube 4. All of 182-190 are the desert "neighbouring cube" toggles; scene 94 (empty water) turns them all on.
- **Written by:** s55 White Leaf Desert, near Dino-fly (=0); s56 White Leaf Desert, near the Camel (=1); s59 White Leaf Desert, near Lighthouse (=1); s60 White Leaf Desert, near J. Baldino's House (=1); s61 White Leaf Desert, at the School of Magic (=1); s94 Empty water outside scene (=1)
- **Island decor:** DESERT cube 4: 1 decor(s), body 1, visible only while set

### 183 - Desert cube 12: coast decor

`decor` - source: curated

- **Values:** `0` = hidden; `1` = shown
- **Notes:** Body 1 in desert cube 12.
- **Written by:** s56 White Leaf Desert, near the Camel (=1); s57 White Leaf Desert, at the Von Kournil tour (=0); s58 White Leaf Desert, near Von Kournil tour (=1); s61 White Leaf Desert, at the School of Magic (=1); s62 White Leaf Desert, near car jump (=1); s63 White Leaf Desert, near Temple of Bù (=1); ... (1 more scenes)
- **Island decor:** DESERT cube 12: 1 decor(s), body 1, visible only while set

### 184 - Desert cube 16: coast decor

`decor` - source: curated

- **Values:** `0` = hidden; `1` = shown
- **Notes:** Body 1 in desert cube 16.
- **Written by:** s57 White Leaf Desert, at the Von Kournil tour (=1); s58 White Leaf Desert, near Von Kournil tour (=0); s62 White Leaf Desert, near car jump (=1); s63 White Leaf Desert, near Temple of Bù (=1); s94 Empty water outside scene (=1)
- **Island decor:** DESERT cube 16: 1 decor(s), body 1, visible only while set

### 185 - Desert cube 1: coast decor

`decor` - source: curated

- **Values:** `0` = hidden; `1` = shown
- **Notes:** Body 1 in desert cube 1.
- **Written by:** s55 White Leaf Desert, near Dino-fly (=1); s59 White Leaf Desert, near Lighthouse (=0); s60 White Leaf Desert, near J. Baldino's House (=1); s64 White Leaf Desert, at the Bell (=1); s65 White Leaf Desert, near Esmer Shuttle (=1); s94 Empty water outside scene (=1)
- **Island decor:** DESERT cube 1: 1 decor(s), body 1, visible only while set

### 186 - Desert cube 17: coast decor

`decor` - source: curated

- **Values:** `0` = hidden; `1` = shown
- **Notes:** Body 1 (two decors) in desert cube 17.
- **Written by:** s57 White Leaf Desert, at the Von Kournil tour (=1); s58 White Leaf Desert, near Von Kournil tour (=1); s62 White Leaf Desert, near car jump (=1); s63 White Leaf Desert, near Temple of Bù (=0); s67 White Leaf Desert, at the Temple of Bù (=1); s68 White Leaf Desert, near Temple of Bù (=1); ... (1 more scenes)
- **Island decor:** DESERT cube 17: 2 decor(s), body 1, visible only while set

### 187 - Desert cube 2: coast decor

`decor` - source: curated

- **Values:** `0` = hidden; `1` = shown
- **Notes:** Body 1 in desert cube 2.
- **Written by:** s59 White Leaf Desert, near Lighthouse (=1); s60 White Leaf Desert, near J. Baldino's House (=1); s64 White Leaf Desert, at the Bell (=0); s65 White Leaf Desert, near Esmer Shuttle (=1); s69 White Leaf Desert, near to the Perl of Incandescence entrance (=1); s70 White Leaf Desert, near Esmer Shuttle (=1); ... (1 more scenes)
- **Island decor:** DESERT cube 2: 1 decor(s), body 1, visible only while set

### 188 - Desert cube 3: coast decor

`decor` - source: curated

- **Values:** `0` = hidden; `1` = shown
- **Notes:** Body 1 in desert cube 3.
- **Written by:** s64 White Leaf Desert, at the Bell (=1); s65 White Leaf Desert, near Esmer Shuttle (=1); s69 White Leaf Desert, near to the Perl of Incandescence entrance (=0); s70 White Leaf Desert, near Esmer Shuttle (=1); s94 Empty water outside scene (=1)
- **Island decor:** DESERT cube 3: 1 decor(s), body 1, visible only while set

### 189 - Desert cube 7: coast decor

`decor` - source: curated

- **Values:** `0` = hidden; `1` = shown
- **Notes:** Body 1 in desert cube 7.
- **Written by:** s64 White Leaf Desert, at the Bell (=1); s65 White Leaf Desert, near Esmer Shuttle (=1); s66 White Leaf Desert, near Bald Mountain (=1); s69 White Leaf Desert, near to the Perl of Incandescence entrance (=1); s70 White Leaf Desert, near Esmer Shuttle (=0); s71 White Leaf Desert, near Bald Mountain (=1); ... (1 more scenes)
- **Island decor:** DESERT cube 7: 1 decor(s), body 1, visible only while set

### 190 - Desert cube 13: decor

`decor` - source: curated

- **Values:** `0` = hidden; `1` = shown
- **Notes:** Body 74 in desert cube 13.
- **Written by:** s56 White Leaf Desert, near the Camel (=1); s57 White Leaf Desert, at the Von Kournil tour (=1); s58 White Leaf Desert, near Von Kournil tour (=1); s61 White Leaf Desert, at the School of Magic (=1); s62 White Leaf Desert, near car jump (=0); s63 White Leaf Desert, near Temple of Bù (=1); ... (3 more scenes)
- **Island decor:** DESERT cube 13: 1 decor(s), body 74, visible only while set

### 191 - Wannies mine: gem near the clover box

`flag` - source: curated

- **Values:** `0` = in place; `1` = taken
- **Notes:** Set in s100; reset at the ferryman (s97).
- **Written by:** s97 Wannies Island, the ferryman landing place scene (=0); s100 Wannies Island, the mine, 2nd room (=1)
- **Read in 1 scene(s):** s100

### 192 - Wannies mine: gem

`flag` - source: curated

- **Values:** `0` = in place; `1` = taken
- **Notes:** Set in s100; reset at the ferryman (s97).
- **Written by:** s97 Wannies Island, the ferryman landing place scene (=0); s100 Wannies Island, the mine, 2nd room (=1)
- **Read in 1 scene(s):** s100

### 193 - Wannies island temple gems taken

`count` - source: curated

- **Notes:** Set 1 then +1 in s112, reset at the ferryman (s97).
- **Written by:** s97 Wannies Island, the ferryman landing place scene (=0); s112 Wannies Island, near the temple on the main (=1, +1)
- **Read in 1 scene(s):** s112

### 194 - Citadel: spaceship / Joe scene at the Sendell sign

`flag` - source: curated

- **Values:** `0` = Not triggered; `1` = Triggered
- **Notes:** Set at track 101 in s47.
- **Written by:** s47 Citadel Island, at the Sendell's sign (=1)
- **Read in 1 scene(s):** s47

### 195 - Dark Monk statue: wizards hole

`flag` - source: curated

- **Values:** `0` = Not triggered; `1` = Triggered
- **Notes:** s185.
- **Written by:** s185 The Dark Monk statue, the wizards, 1st scene (=1)
- **Read in 1 scene(s):** s185

## Engine-reserved and late flags (235-255)

### 235 - Movies seen: bits 0-15 of the ACF list

`engine` - `FLAG_ACF` - source: curated

- **Notes:** Bit n of 235 + n / 16 is set by PlayAcf when the n-th ACF movie is played (PLAYACF.CPP); PlayAllAcf and the memory viewer (item 24) use it to replay movies (the INTRO is skipped). The trainer's per-value descriptions are movie combinations, not stages.

### 236 - Movies seen: bits 16-31

`engine` - `FLAG_ACF2` - source: curated

- **Notes:** As 235.

### 237 - Movies seen: bits 32-47

`engine` - `FLAG_ACF3` - source: curated

- **Notes:** As 235.

### 240 - Clover box: Citadel sewers

`flag` - source: curated

- **Values:** `0` = in place; `1` = taken
- **Notes:** The box actor tests it on entry and removes itself when set.
- **Written by:** s17 Citadel Island, Sewer (below Downtown) (=1)
- **Read in 1 scene(s):** s17

### 241 - Clover box: Dome of the Slate

`flag` - source: curated

- **Values:** `0` = in place; `1` = taken
- **Written by:** s26 Citadel Island, Dome of the Slate (=1)
- **Read in 1 scene(s):** s26

### 242 - Clover box: Wannies mine 2nd room (read only)

`flag` - source: curated

- **Values:** `0` = in place; `1` = taken
- **Notes:** Only read (s100 actor 5) and never set by any script.
- **Read in 1 scene(s):** s100

### 243 - Clover box: Desert island (via the turtle)

`flag` - source: curated

- **Values:** `0` = in place; `1` = taken
- **Written by:** s55 White Leaf Desert, near Dino-fly (=1)
- **Read in 1 scene(s):** s55

### 244 - Clover box: Island under Celebration and Island CX

`flag` - source: curated

- **Values:** `0` = in place; `1` = taken
- **Notes:** Set from the hide-out (s129) and the CX secret passage (s180).
- **Written by:** s129 Island Under Celebration, the hide out (=1); s180 Island CX, Secret passage (=1)
- **Read in 2 scene(s):** s129, s180

### 245 - Clover box: Emerald Moon

`flag` - source: curated

- **Values:** `0` = in place; `1` = taken
- **Written by:** s13 Emerald Moon, Outside Baldino's cell(room #15) (=1)
- **Read in 1 scene(s):** s13

### 246 - Clover box: island across from the hacienda

`flag` - source: curated

- **Values:** `0` = in place; `1` = taken
- **Written by:** s73 White Leaf Desert, at the island across from the Hacienda (=1)
- **Read in 1 scene(s):** s73

### 248 - Demo only (demo only)

`flag` - source: curated

- **Notes:** Only written in the demo scene 193.

### 249 - Scenario signal: cut scene skipped with ESC

`engine` - `FLAG_ESC` - source: curated

- **Values:** `0` = normal; `1` = the player pressed ESC in a cinema scene
- **Notes:** The engine sets it in ResetCinemaMode (OBJECT.CPP) and clears it when CinemaMode switches on (GERELIFE.CPP). Scripts test it after a cut scene to jump to its end. Scripts also reset it to 0 (15 writers). The trainer's "took ferry" descriptions came from watching this.
- **Written by:** s43 Citadel Island, near the Harbor (=0); s44 Citadel Island, outside the Dome of the Slate (=0); s45 Citadel Island, near the tent of the Weather Wizard (=0); s46 Citadel Island, near Lighthouse (=0); s48 Citadel Island, downstairs the sewer grid (=0); s49 Citadel Island, near Dino-Fly (=0); ... (4 more scenes)
- **Read in 18 scene(s):** s43, s44, s45, s46, s48, s49, s50, s55, s65, s73, s87, s95, s97, s105, ...

### 251 - Clover leaves (current life boxes)

`count` - `FLAG_CLOVER` - source: curated

- **Notes:** Quantity of clover leaves the hero has; capped at NbCloverBox and refilled by the engine (COMPORTE.CPP, OBJECT.CPP, EXTRA.CPP). Scripts only read it (== 0 in 8 places) and grow the cap with INC_CLOVER_BOX. The trainer's "Number of lives" is this.
- **Read in 1 scene(s):** s12

### 252 - Vehicle taken

`engine` - `FLAG_VEHICULE_PRIS` - source: curated

- **Notes:** Named in COMMON.H and the console ("vehicletaken") but nothing in the engine or scripts reads or writes it.

### 253 - Chapter

`engine` - `FLAG_CHAPTER` - source: curated

- **Values:**
  - `0` = initial
  - `1` = new game (s0)
  - `2` = aliens land (s46, lighthouse)
  - `3` = wizard to Otringal (s41)
  - `4` = returned to the Citadel (s47)
  - `5` = going to the Emerald Moon (s114)
  - `6` = Emerald Moon, X-symbol building (s53)
  - `7` = Wannies elevator scene (s98)
  - `8` = mosquibee passage to CX (s176)
  - `9` = Island CX shuttle (s182)
  - `10` = key fragments used in the Celebration temple (s93)
- **Notes:** Each value is set by the scene named after it (SET_VAR_GAME(253, n); INC_CHAPTER is never used). The trainer's labels for 6 to 10 describe the same moments. LF_CHAPTER reads it. MAX_CHAPTER = 20: the engine indexes NecessaryChapterMoney[] (money bonus cap, 150 in chapter 4) and MessChapitreTwinsen[][] (hero's chapter idle chatter) with it; TEMPETE_ACTIVE is chapter < 2 or rain.
- **Written by:** s0 Citadel Island, Twinsen's house (=1); s41 White Leaf Desert, Esmer Shuttle (=3); s46 Citadel Island, near Lighthouse (=2); s47 Citadel Island, at the Sendell's sign (=4); s53 Emerald Moon, entrance X symbol building (=6); s93 Celebration Island, inside the temple (=10); ... (4 more scenes)
- **Read in 76 scene(s):** s0, s1, s3, s4, s5, s7, s8, s9, s10, s12, s14, s15, s17, s20, ...

### 254 - Esmer rails (mine wagon uses Esmer rail bricks)

`engine` - `FLAG_PLANETE_ESMER` - source: curated

- **Values:** `0` = Twinsun rails; `1` = Esmer rails
- **Notes:** GetNumBrickWagon (WAGON.CPP) swaps every RAIL_E_* brick for the plain rail when set. Written by the mine scenes: 1 in the Wannies mine (s100, s122), 0 in the Temple of Bu (s10, s11). The trainer's "entered mine" is wrong.
- **Written by:** s10 White Leaf Desert, Temple of Bú, 1st scene (=0); s11 White Leaf Desert, Temple of Bú, 2nd scene (=0); s100 Wannies Island, the mine, 2nd room (=1); s122 Wannies Island, the mine, 1st room (=1)

### 255 - Reserved (inventory sentinel)

`engine` - `FLAG_DONT_USE` - source: curated

- **Notes:** Used as the "empty" marker in TabIndir; never a real variable.

## Unused indices

No script, engine or decor references these indices in the retail data: 58, 75, 96, 114, 196-234, 238-239, 247, 250. They are free for new content (script-created flags in an editor should start above 195, avoiding 235-237 and 249-255).

