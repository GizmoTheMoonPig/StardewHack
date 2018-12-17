﻿using System;
using System.Reflection.Emit;
using StardewModdingAPI;

namespace StardewHack.HarvestWithScythe
{
    public class ModConfig {
        /** Should the game be patched to allow harvesting forage with the scythe? */
        public bool HarvestForage = true;
        /** Should quality be applied to additional harvest? */
        public bool AllHaveQuality = false;
        /** Whether crops should also remain pluckable by hand. */
        public bool AllowManualHarvest = true;
    }

    public class ModEntry : HackWithConfig<ModEntry, ModConfig>
    {
        [BytecodePatch("StardewValley.Crop::harvest")]
        void Crop_harvest() {
            #region Fix vector
            Harmony.CodeInstruction ins = null;
            // Remove line (2x)
            // Vector2 vector = new Vector2 ((float)xTile, (float)yTile);
            for (int i = 0; i < 2; i++) {
                var vec = FindCode (
                    OpCodes.Ldloca_S,
                    OpCodes.Ldarg_1,
                    OpCodes.Conv_R4,
                    OpCodes.Ldarg_2,
                    OpCodes.Conv_R4,
                    OpCodes.Call
                );
                ins = vec[5];
                vec.Remove();
            }
            
            // Add to begin of function
            // Vector2 vector = new Vector2 ((float)xTile*64., (float)yTile*64.);
            BeginCode().Append(
                Instructions.Ldloca_S(3),
                Instructions.Ldarg_1(),
                Instructions.Conv_R4(),
                Instructions.Ldc_R4(64),
                Instructions.Mul(),
                Instructions.Ldarg_2(),
                Instructions.Conv_R4(),
                Instructions.Ldc_R4(64),
                Instructions.Mul(),
                ins
            );
            
            // Replace (4x):
            //   from: new Vector2 (vector.X * 64f, vector.Y * 64f)
            //   to:   vector
            for (int i = 0; i < 4; i++) {
                FindCode(
                    null,
                    OpCodes.Ldfld,
                    Instructions.Ldc_R4(64),
                    OpCodes.Mul,
                    null,
                    OpCodes.Ldfld,
                    Instructions.Ldc_R4(64),
                    OpCodes.Mul,
                    OpCodes.Newobj
                ).Replace(
                    Instructions.Ldloc_3() // vector
                );
            }
            #endregion

            #region Support harvesting of spring onions with scythe
            // Find the lines:
            var AddItem = FindCode(
                // if (Game1.player.addItemToInventoryBool (@object, false)) {
                Instructions.Call_get(typeof(StardewValley.Game1), "player"),
                OpCodes.Ldloc_0,
                OpCodes.Ldc_I4_0,
                Instructions.Callvirt(typeof(StardewValley.Farmer), "addItemToInventoryBool", typeof(StardewValley.Item), typeof(bool)),
                OpCodes.Brfalse
            );

            // Make jumps to the start of AddItem jump to the start of "Vector2 vector = ..."
            var ldarg0 = Instructions.Ldarg_0();
            AddItem.ReplaceJump(0, ldarg0);

            // Swap the lines (add '*64' to vector) &
            // Insert check for harvesting with scythe and act accordingly.
            AddItem.Prepend(
                // if (this.harvestMethod != 0) {
                ldarg0,
                Instructions.Ldfld(typeof(StardewValley.Crop), "harvestMethod"),
                Instructions.Call_get(typeof(Netcode.NetInt), "Value"), // this.indexOfHarvest
                Instructions.Brfalse(AttachLabel(AddItem[0])),
                // Game1.createItemDebris (@object, vector, -1, null, -1)
                Instructions.Ldloc_0(), // @object
                Instructions.Ldloc_3(), // vector
                Instructions.Ldc_I4_M1(), // -1
                Instructions.Ldnull(), // null
                Instructions.Ldc_I4_M1(), // -1
                Instructions.Call(typeof(StardewValley.Game1), "createItemDebris", typeof(StardewValley.Item), typeof(Microsoft.Xna.Framework.Vector2), typeof(int), typeof(StardewValley.GameLocation), typeof(int)),
                // Game1.player.gainExperience (2, howMuch);
                Instructions.Call_get(typeof(StardewValley.Game1), "player"),
                Instructions.Ldc_I4_2(),
                Instructions.Ldloc_1(),
                Instructions.Callvirt(typeof(StardewValley.Farmer), "gainExperience", typeof(int), typeof(int)),
                // return true
                Instructions.Ldc_I4_1(),
                Instructions.Ret()
                // }
            );
            #endregion

            // >>> Patch code to drop sunflower seeds when harvesting with scythe.
            // >>> Patch code to let harvesting with scythe drop only 1 item.
            // >>> The other item drops are handled by the plucking code.
            #region Sunflower drops 

            // Remove start of loop
            FindCode(
                OpCodes.Ldc_I4_0,
                Instructions.Stloc_S(12),
                OpCodes.Br
            ).Remove();

            // Find the start of the 'drop sunflower seeds' part.
            var DropSunflowerSeeds = FindCode(
                OpCodes.Ldarg_0,
                Instructions.Ldfld(typeof(StardewValley.Crop), "indexOfHarvest"),
                OpCodes.Call, // Netcode
                Instructions.Ldc_I4(421), // 421 = Item ID of Sunflower.
                OpCodes.Bne_Un
            );
            // Set quality for seeds to 0.
            DropSunflowerSeeds.Append(
                Instructions.Ldc_I4_0(),
                Instructions.Stloc_S(5)
            );

            // Remove end of loop and everything after that until the end of the harvest==1 branch.
            var ScytheBranchTail = FindCode(
                OpCodes.Ldarg_0,
                Instructions.Ldfld(typeof(StardewValley.Crop), "harvestMethod"),
                OpCodes.Call, // Netcode
                OpCodes.Ldc_I4_1,
                OpCodes.Bne_Un
            ).Follow(4);
            ScytheBranchTail.ExtendBackwards(
                Instructions.Ldloc_S(12),
                OpCodes.Ldc_I4_1,
                OpCodes.Add,
                Instructions.Stloc_S(12),
                Instructions.Ldloc_S(12),
                Instructions.Ldloc_S(4),
                OpCodes.Blt
            );
            
            // Change jump to end of loop into jump to drop sunflower seeds.
            ScytheBranchTail.ReplaceJump(0, DropSunflowerSeeds[0]);

            // Rewrite the tail of the Scythe harvest branch. 
            ScytheBranchTail.Replace(
                // Jump to the 'drop subflower seeds' part.
                Instructions.Br(AttachLabel(DropSunflowerSeeds[0]))
            );
            #endregion

            #region Colored flowers
            
            
            #endregion

            if (config.AllHaveQuality) {
                // Patch function calls for additional harvest to pass on the harvest quality.
                FindCode(
                    OpCodes.Ldc_I4_M1,
                    OpCodes.Ldc_I4_0,
                    Instructions.Ldc_R4(1.0f),
                    OpCodes.Ldnull,
                    Instructions.Call(typeof(StardewValley.Game1), "createObjectDebris", typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(float), typeof(StardewValley.GameLocation))
                )[1] = Instructions.Ldloc_S(5);

                FindCode(
                    OpCodes.Ldc_I4_1,
                    OpCodes.Ldc_I4_0,
                    OpCodes.Ldc_I4_M1,
                    OpCodes.Ldc_I4_0,
                    OpCodes.Newobj,
                    Instructions.Callvirt(typeof(StardewValley.Characters.JunimoHarvester), "tryToAddItemToHut", typeof(StardewValley.Item))
                )[3] = Instructions.Ldloc_S(5);
            }
        }

        // Proxy method for creating an object suitable for spawning as debris.
        public static StardewValley.Object CreateObject(StardewValley.Crop crop, int quality) {
            if (crop.programColored) {
                return new StardewValley.Objects.ColoredObject (crop.indexOfHarvest, 1, crop.tintColor) {
                    Quality = quality
                };
            } else {
                return new StardewValley.Object(crop.indexOfHarvest, 1, false, -1, quality);
            }
        }

        // Note: the branch
        //   if (this.forageCrop)
        // refers mainly to the crop spring union.
        // Harvesting those with scythe behaves a bit odd.

        [BytecodePatch("StardewValley.TerrainFeatures.HoeDirt::performToolAction")]
        void HoeDirt_performToolAction() {
            // Find the first harvestMethod==1 check.
            var HarvestMethodCheck = FindCode(
                OpCodes.Ldarg_0,
                Instructions.Call_get(typeof(StardewValley.TerrainFeatures.HoeDirt), "crop"),
                Instructions.Ldfld(typeof(StardewValley.Crop), "harvestMethod"),
                OpCodes.Call, // Netcode
                OpCodes.Ldc_I4_1,
                OpCodes.Bne_Un
            );

            // Change the harvestMethod==1 check to damage=harvestMethod; harvestMethod=1
            HarvestMethodCheck.Replace(
                // damage = crop.harvestMethod.
                HarvestMethodCheck[0],
                HarvestMethodCheck[1],
                HarvestMethodCheck[2],
                HarvestMethodCheck[3],
                Instructions.Starg_S(2), // damage

                // crop.harvestMethod = 1
                HarvestMethodCheck[0],
                HarvestMethodCheck[1],
                HarvestMethodCheck[2],
                Instructions.Ldc_I4_1(),
                Instructions.Call_set(typeof(Netcode.NetInt), "Value")
            );

            // Set harvestMethod=damage after the following crop!=null check.
            HarvestMethodCheck.FindNext(
                OpCodes.Ldarg_0,
                Instructions.Call_get(typeof(StardewValley.TerrainFeatures.HoeDirt), "crop"),
                Instructions.Ldfld(typeof(StardewValley.Crop), "dead"),
                OpCodes.Call, // Netcode
                OpCodes.Brfalse
            ).Prepend(
                HarvestMethodCheck[0],
                HarvestMethodCheck[1],
                HarvestMethodCheck[2],
                Instructions.Ldarg_2(), // damage
                Instructions.Call_set(typeof(Netcode.NetInt), "Value")
            );
        }

        public bool HarvestForageEnabled() {
            return config.HarvestForage;
        }


        [BytecodePatch("StardewValley.Object::performToolAction", "HarvestForageEnabled")]
        void Object_performToolAction() {
            var code = BeginCode();
            Label begin = AttachLabel(code[0]);
            code.Prepend(
                // Check if Tool is scythe.
                Instructions.Ldarg_1(),
                Instructions.Isinst(typeof(StardewValley.Tools.MeleeWeapon)),
                Instructions.Brfalse(begin),
                Instructions.Ldarg_1(),
                Instructions.Isinst(typeof(StardewValley.Tools.MeleeWeapon)),
                Instructions.Callvirt_get(typeof(StardewValley.Tool), "BaseName"),
                Instructions.Ldstr("Scythe"),
                Instructions.Callvirt(typeof(System.String), "Equals", typeof(string)),
                Instructions.Brfalse(begin),
                // Hook
                Instructions.Ldarg_0(),
                Instructions.Ldarg_1(),
                Instructions.Ldarg_2(),
                Instructions.Call(typeof(ModEntry), "ScytheForage", typeof(StardewValley.Object), typeof(StardewValley.Tool), typeof(StardewValley.GameLocation)),
                Instructions.Brfalse(begin),
                Instructions.Ldc_I4_1(),
                Instructions.Ret()
            );
        }

        public static bool ScytheForage(StardewValley.Object o, StardewValley.Tool t, StardewValley.GameLocation loc) {
            if (o.isSpawnedObject && !o.questItem && o.isForage(loc)) {
                var who = t.getLastFarmerToUse();
                var vector = o.TileLocation; 
                int quality = o.quality;
                Random random = new Random((int)StardewValley.Game1.uniqueIDForThisGame / 2 + (int)StardewValley.Game1.stats.DaysPlayed + (int)vector.X + (int)vector.Y * 777);
                if (who.professions.Contains(16)) {
                    quality = 4;
                } else if (random.NextDouble() < (double)((float)who.ForagingLevel / 30)) {
                    quality = 2;
                } else if (random.NextDouble() < (double)((float)who.ForagingLevel / 15)) {
                    quality = 1;
                }
                who.gainExperience(2, 7);
                StardewValley.Game1.createObjectDebris(o.ParentSheetIndex, (int)vector.X, (int)vector.Y, -1, quality, 1, loc);
                StardewValley.Game1.stats.ItemsForaged += 1;
                if (who.professions.Contains(13) && random.NextDouble() < 0.2) {
                    StardewValley.Game1.createObjectDebris(o.ParentSheetIndex, (int)vector.X, (int)vector.Y, -1, quality, 1, loc);
                    who.gainExperience(2, 7);
                }
                return true;
            } else {
                return false;
            }
        }
    }
}

