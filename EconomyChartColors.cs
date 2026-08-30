// LWF Economy Graph — アイコンの代表色（自動生成）
//
// 自動生成。**手で編集しないこと**（作り直すと消える）。
//
// 実機でテクスチャの画素を読むには isReadable が要り、ゲームの絵は false なので、
// アイコンの代表色は先に求めて焼き込んである。
// レシピは「そのレシピが作る物」の絵から取っている（ゲーム内のアイコンの引き方と同じ）。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace LwfEconomyGraph
{
    /// <summary>アイコンの代表色。無いIDは呼び出し側の既定色に落ちる。</summary>
    internal static class ChartColors
    {
        private static Dictionary<string, Color> _map;

        internal static bool TryGet(string id, out Color color)
        {
            color = Color.white;
            if (string.IsNullOrEmpty(id)) { return false; }
            if (_map == null) { Build(); }
            return _map.TryGetValue(id, out color);
        }

        private static void Build()
        {
            _map = new Dictionary<string, Color>(Ids.Length, StringComparer.Ordinal);
            for (int i = 0; i < Ids.Length; i++)
            {
                uint packed = Rgb[i];
                _map[Ids[i]] = new Color(
                    ((packed >> 16) & 0xFF) / 255f,
                    ((packed >> 8) & 0xFF) / 255f,
                    (packed & 0xFF) / 255f,
                    1f);
            }
        }

        private static readonly string[] Ids = new string[]
        {
            "Adamantite",
            "All",
            "AllCritByTime",
            "Amethyst",
            "BuffGemMagic",
            "CancelGetFam",
            "Cash",
            "CashByLandTime",
            "Chemical",
            "Cinnabar",
            "Cobalt",
            "Coin",
            "Compost",
            "Construction",
            "ConvertOnRAllFuel",
            "Conveyor2Supplies",
            "ConveyorSpeedByTime",
            "ConveyorSupplies",
            "Crafter",   // Summon-Crafter の絵から
            "CrimeCoin",
            "CrimeContract",
            "CrimeLand",
            "Debug1",   // Orichalcum の絵から
            "Debug2",   // Pickaxe の絵から
            "Debug3",   // Lye の絵から
            "Debug4",   // Coin の絵から
            "Debug5",   // MagicChunk の絵から
            "FakeCoin",
            "FakeStamp",
            "FamExpense",
            "FamIncome",
            "FasterOrder",
            "Fertilizer",
            "FreeLand",
            "FreeLandSupplies",
            "Fuel",
            "GemPowder",
            "Gold",
            "GoldOre",
            "Grocery",
            "Gunpowder",
            "HDAStamp",
            "Honey",
            "Hourglass",
            "IntervalChemical",
            "IntervalCrafter",
            "IntervalFuel",
            "IntervalGrocery",
            "IntervalMagic",
            "IntervalSmelter",
            "IntervalSummoner",
            "IntervalTransporter",
            "IntervalWorker",
            "Iron",
            "IronOre",
            "Jade",
            "Lemon",
            "Lemonade",
            "LoseTransportSpeed",
            "Luxury",
            "Lye",
            "Magic",
            "MagicChunk",
            "MagicToCash",   // Coin の絵から
            "Mercury",
            "Monitoring",
            "Mushroom",
            "Mythril",
            "Nitro",
            "Oil",
            "Olive",
            "OnDeliveryPatronResources",
            "OnPurchaseTag1",
            "OnPurchaseTag6",
            "OrderTag6ByAll",
            "Orichalcum",
            "Pickaxe",
            "ReduceSkillCost",
            "RelicAllSmelter",
            "RelicAmethyst",
            "RelicCobalt",
            "RelicCritCash",
            "RelicJade",
            "RelicLye",
            "RelicOrderTag1",
            "RelicOrderTag6",
            "RelicRepaymentTag4",
            "RelicRuby",
            "RelicTag2X10",
            "RelicTag6X10",
            "RepaymentLoseCash",
            "RoyalStraightFlush",   // Mythril の絵から
            "Ruby",
            "Salt",
            "Smelter",   // Summon-Smelter の絵から
            "SmelterSpeedByTime",
            "Smuggling",
            "Soda",
            "Splitter",   // Summon-Splitter の絵から
            "Stamp",
            "Sulfur",
            "Summon-Caretaker",
            "Summon-Conveyor",
            "Summon-Conveyor2",
            "Summon-Crafter",
            "Summon-Farmer",
            "Summon-Smelter",
            "Summon-Splitter",
            "Summon-Splitter2",
            "Summon-Summoner",
            "Summon-Transporter",
            "Summon-TransporterExit",
            "Summon-Worker",
            "Summoner",   // Summon-Summoner の絵から
            "SummonerSpeed",
            "SusPowder",
            "TradeCrafterAmethystCoin",   // Coin の絵から
            "TradeCrafterAmethystFuelGold",   // Gold の絵から
            "TradeCrafterAmethystWoodGold",   // Gold の絵から
            "TradeCrafterCinnabarFuelSoda",   // Soda の絵から
            "TradeCrafterCinnabarGold",   // Gold の絵から
            "TradeCrafterCobaltFuelMercury",   // Mercury の絵から
            "TradeCrafterCobaltSoda",   // Soda の絵から
            "TradeCrafterCobaltWoodMercury",   // Mercury の絵から
            "TradeCrafterCoinFuelGold",   // Gold の絵から
            "TradeCrafterCoinWoodGold",   // Gold の絵から
            "TradeCrafterComplexCoin",   // Coin の絵から
            "TradeCrafterComplexNitro",   // Nitro の絵から
            "TradeCrafterComplexPickaxe",   // Pickaxe の絵から
            "TradeCrafterComplexSoda",   // Soda の絵から
            "TradeCrafterConstructionSplitter2",   // Summon-Splitter2 の絵から
            "TradeCrafterFuelCoin",   // Coin の絵から
            "TradeCrafterFuelSplitter2",   // Summon-Splitter2 の絵から
            "TradeCrafterGoldOreCoin",   // Coin の絵から
            "TradeCrafterIronCoin",   // Coin の絵から
            "TradeCrafterIronOreFuelPickaxe",   // Pickaxe の絵から
            "TradeCrafterIronOreGold",   // Gold の絵から
            "TradeCrafterJadeFuelIron",   // Iron の絵から
            "TradeCrafterJadePickaxe",   // Pickaxe の絵から
            "TradeCrafterJadeWoodIron",   // Iron の絵から
            "TradeCrafterLuxurySplitter2",   // Summon-Splitter2 の絵から
            "TradeCrafterMagicSplitter2",   // Summon-Splitter2 の絵から
            "TradeCrafterOliveFuelGunpowder",   // Gunpowder の絵から
            "TradeCrafterOliveWoodGunpowder",   // Gunpowder の絵から
            "TradeCrafterRubyFuelGunpowder",   // Gunpowder の絵から
            "TradeCrafterRubyNitro",   // Nitro の絵から
            "TradeCrafterRubyWoodGunpowder",   // Gunpowder の絵から
            "TradeCrafterSaltMercury",   // Mercury の絵から
            "TradeCrafterSulfurFuelGunpowder",   // Gunpowder の絵から
            "TradeCrafterSulfurNitro",   // Nitro の絵から
            "TradeCrafterSulfurWoodGunpowder",   // Gunpowder の絵から
            "TradeCrafterWaxFuelGunpowder",   // Gunpowder の絵から
            "TradeCrafterWaxWoodGunpowder",   // Gunpowder の絵から
            "TradeCrafterWoodCoin",   // Coin の絵から
            "TradeCrafterWoodIron",   // Iron の絵から
            "TradeSmelterBlueSoda",   // Soda の絵から
            "TradeSmelterChemicalCinnabar",   // Cinnabar の絵から
            "TradeSmelterChemicalCompost",   // Compost の絵から
            "TradeSmelterChemicalConveyor",   // Summon-Conveyor の絵から
            "TradeSmelterChemicalGemPowder",   // GemPowder の絵から
            "TradeSmelterChemicalLemonade",   // Lemonade の絵から
            "TradeSmelterChemicalLye",   // Lye の絵から
            "TradeSmelterChemicalSalt",   // Salt の絵から
            "TradeSmelterChemicalSulfur",   // Sulfur の絵から
            "TradeSmelterConstructionCompost",   // Compost の絵から
            "TradeSmelterConstructionConveyor",   // Summon-Conveyor の絵から
            "TradeSmelterConstructionGemPowder",   // GemPowder の絵から
            "TradeSmelterConstructionLemonade",   // Lemonade の絵から
            "TradeSmelterConstructionLye",   // Lye の絵から
            "TradeSmelterConstructionSoda",   // Soda の絵から
            "TradeSmelterConveyorLye",   // Lye の絵から
            "TradeSmelterFertilizerCompost",   // Compost の絵から
            "TradeSmelterFertilizerConveyor",   // Summon-Conveyor の絵から
            "TradeSmelterFertilizerGemPowder",   // GemPowder の絵から
            "TradeSmelterFertilizerLemonade",   // Lemonade の絵から
            "TradeSmelterFertilizerLye",   // Lye の絵から
            "TradeSmelterFertilizerSoda",   // Soda の絵から
            "TradeSmelterFromTransporter",   // Summon-Smelter の絵から
            "TradeSmelterFromWorker",   // Summon-Smelter の絵から
            "TradeSmelterFuelCompost",   // Compost の絵から
            "TradeSmelterFuelConveyor",   // Summon-Conveyor の絵から
            "TradeSmelterFuelGemPowder",   // GemPowder の絵から
            "TradeSmelterFuelLemonade",   // Lemonade の絵から
            "TradeSmelterFuelLye",   // Lye の絵から
            "TradeSmelterFuelSoda",   // Soda の絵から
            "TradeSmelterGoldSulfur",   // Sulfur の絵から
            "TradeSmelterGroceryCompost",   // Compost の絵から
            "TradeSmelterGroceryConveyor",   // Summon-Conveyor の絵から
            "TradeSmelterGroceryGemPowder",   // GemPowder の絵から
            "TradeSmelterGroceryLemonade",   // Lemonade の絵から
            "TradeSmelterGroceryLye",   // Lye の絵から
            "TradeSmelterGrocerySoda",   // Soda の絵から
            "TradeSmelterIronOreCinnabar",   // Cinnabar の絵から
            "TradeSmelterIronOreSalt",   // Salt の絵から
            "TradeSmelterLuxuryCompost",   // Compost の絵から
            "TradeSmelterLuxuryConveyor",   // Summon-Conveyor の絵から
            "TradeSmelterLuxuryGemPowder",   // GemPowder の絵から
            "TradeSmelterLuxuryLemonade",   // Lemonade の絵から
            "TradeSmelterLuxuryLye",   // Lye の絵から
            "TradeSmelterLuxurySoda",   // Soda の絵から
            "TradeSmelterMagicCompost",   // Compost の絵から
            "TradeSmelterMagicConveyor",   // Summon-Conveyor の絵から
            "TradeSmelterMagicGemPowder",   // GemPowder の絵から
            "TradeSmelterMagicLemonade",   // Lemonade の絵から
            "TradeSmelterMagicLye",   // Lye の絵から
            "TradeSmelterMagicSoda",   // Soda の絵から
            "TradeSmelterNoneMushroom",   // Mushroom の絵から
            "TradeSmelterWoodMushroom",   // Mushroom の絵から
            "TradeSummoneCreateFam2",   // Summon-Worker の絵から
            "TradeSummoneCreateFam4",   // Summon-Transporter の絵から
            "TradeSummoneCreateFam6",   // Summon-Smelter の絵から
            "TradeSummoneCreateFam8",   // Summon-Crafter の絵から
            "TradeSummonerAdamantite",   // Adamantite の絵から
            "TradeSummonerAmethystFromBuff",   // Amethyst の絵から
            "TradeSummonerAmethystIncrease",   // Amethyst の絵から
            "TradeSummonerAmethystRock",   // Amethyst の絵から
            "TradeSummonerAmethystTag",   // Amethyst の絵から
            "TradeSummonerCobaltFromBuff",   // Cobalt の絵から
            "TradeSummonerCobaltIncrease",   // Cobalt の絵から
            "TradeSummonerCobaltRock",   // Cobalt の絵から
            "TradeSummonerCobaltTag",   // Cobalt の絵から
            "TradeSummonerJadeFromBuff",   // Jade の絵から
            "TradeSummonerJadeIncrease",   // Jade の絵から
            "TradeSummonerJadeRock",   // Jade の絵から
            "TradeSummonerJadeTag",   // Jade の絵から
            "TradeSummonerMythril",   // Mythril の絵から
            "TradeSummonerRubyFromBuff",   // Ruby の絵から
            "TradeSummonerRubyIncrease",   // Ruby の絵から
            "TradeSummonerRubyRock",   // Ruby の絵から
            "TradeSummonerRubyTag",   // Ruby の絵から
            "Transporter",   // Summon-Transporter の絵から
            "Wand",
            "Wax",
            "WingPen",
            "Wood",
            "Worker",   // Summon-Worker の絵から
            "WorkerIncome",
            "WorkerSpeedByTime",
        };

        private static readonly uint[] Rgb = new uint[]
        {
            0xE35A33u,
            0xC77E4Fu,
            0xC78421u,
            0x8754C7u,
            0xC74B59u,
            0xC71D2Eu,
            0xC79D5Cu,
            0xC7A16Du,
            0x3671C7u,
            0xC73B77u,
            0x2459D0u,
            0xE5B436u,
            0xC7816Du,
            0x6AC759u,
            0xCC4A32u,
            0x00C767u,
            0x059AC7u,
            0x0196C7u,
            0xD27175u,
            0xC73339u,
            0xC73339u,
            0xC7A06Du,
            0x6EDBE3u,
            0xC7876Du,
            0x63ADDFu,
            0xE5B436u,
            0x6DC7B2u,
            0xF5B527u,
            0x8A3CF7u,
            0xC75048u,
            0xC79F6Du,
            0xC74337u,
            0xC735B9u,
            0xC7A06Du,
            0xC8A16Eu,
            0xCA4931u,
            0x53BCC7u,
            0xFEDE72u,
            0xC78320u,
            0xC79E35u,
            0x6DA1C7u,
            0xDF6368u,
            0xF5D275u,
            0xDA3038u,
            0x5087D2u,
            0x6D96C7u,
            0xF39E16u,
            0xC77A4Du,
            0xC7515Bu,
            0x6DBBC7u,
            0xC7AB62u,
            0x806DC7u,
            0xF1C152u,
            0x85C76Du,
            0x83C76Du,
            0x51C748u,
            0xF2DF5Du,
            0xE2BD51u,
            0x84C76Du,
            0x8667C7u,
            0x63ADDFu,
            0x2BC7C7u,
            0x6DC7B2u,
            0xE5B436u,
            0x6DA3C7u,
            0xC76D85u,
            0x6289C7u,
            0xC486F8u,
            0xC74D40u,
            0xF5D276u,
            0xAFC74Au,
            0x0C99C7u,
            0x866DC7u,
            0x648AC7u,
            0x6DC0C7u,
            0x6EDBE3u,
            0xC7876Du,
            0x2AC7C7u,
            0x6DBFC7u,
            0x8855C7u,
            0x2357CCu,
            0xC76557u,
            0x51C748u,
            0x134EF3u,
            0xC78E31u,
            0x6DBFC7u,
            0x9322C7u,
            0xD24C5Bu,
            0x7AC76Du,
            0xEF7162u,
            0xC74C44u,
            0xC486F8u,
            0xDA4C5Cu,
            0xE17C95u,
            0x6DC1C7u,
            0x7FC765u,
            0xC5BFC7u,
            0x62D1F9u,
            0x00CBEFu,
            0xC77259u,
            0xF9BD11u,
            0xC7BA6Du,
            0x0094C7u,
            0x00C766u,
            0xD27175u,
            0xFAF5F7u,
            0x6DC1C7u,
            0x00CBEFu,
            0x01C767u,
            0x6D77C7u,
            0xBB6EC8u,
            0xC7746Du,
            0xC79B51u,
            0x6D77C7u,
            0x27BBC7u,
            0x5935C7u,
            0xE5B436u,
            0xFEDE72u,
            0xFEDE72u,
            0x62D1F9u,
            0xFEDE72u,
            0x6DA3C7u,
            0x62D1F9u,
            0x6DA3C7u,
            0xFEDE72u,
            0xFEDE72u,
            0xE5B436u,
            0xC74D40u,
            0xC7876Du,
            0x62D1F9u,
            0x01C767u,
            0xE5B436u,
            0x01C767u,
            0xE5B436u,
            0xE5B436u,
            0xC7876Du,
            0xFEDE72u,
            0x85C76Du,
            0xC7876Du,
            0x85C76Du,
            0x01C767u,
            0x01C767u,
            0x6DA1C7u,
            0x6DA1C7u,
            0x6DA1C7u,
            0xC74D40u,
            0x6DA1C7u,
            0x6DA3C7u,
            0x6DA1C7u,
            0xC74D40u,
            0x6DA1C7u,
            0x6DA1C7u,
            0x6DA1C7u,
            0xE5B436u,
            0x85C76Du,
            0x62D1F9u,
            0xC73B77u,
            0xC7816Du,
            0x0094C7u,
            0x53BCC7u,
            0xE2BD51u,
            0x63ADDFu,
            0xE17C95u,
            0xF9BD11u,
            0xC7816Du,
            0x0094C7u,
            0x53BCC7u,
            0xE2BD51u,
            0x63ADDFu,
            0x62D1F9u,
            0x63ADDFu,
            0xC7816Du,
            0x0094C7u,
            0x53BCC7u,
            0xE2BD51u,
            0x63ADDFu,
            0x62D1F9u,
            0x6DC1C7u,
            0x6DC1C7u,
            0xC7816Du,
            0x0094C7u,
            0x53BCC7u,
            0xE2BD51u,
            0x63ADDFu,
            0x62D1F9u,
            0xF9BD11u,
            0xC7816Du,
            0x0094C7u,
            0x53BCC7u,
            0xE2BD51u,
            0x63ADDFu,
            0x62D1F9u,
            0xC73B77u,
            0xE17C95u,
            0xC7816Du,
            0x0094C7u,
            0x53BCC7u,
            0xE2BD51u,
            0x63ADDFu,
            0x62D1F9u,
            0xC7816Du,
            0x0094C7u,
            0x53BCC7u,
            0xE2BD51u,
            0x63ADDFu,
            0x62D1F9u,
            0x6289C7u,
            0x6289C7u,
            0xC79B51u,
            0xBB6EC8u,
            0x6DC1C7u,
            0xD27175u,
            0xE35A33u,
            0x8754C7u,
            0x8754C7u,
            0x8754C7u,
            0x8754C7u,
            0x2459D0u,
            0x2459D0u,
            0x2459D0u,
            0x2459D0u,
            0x51C748u,
            0x51C748u,
            0x51C748u,
            0x51C748u,
            0xC486F8u,
            0xDA4C5Cu,
            0xDA4C5Cu,
            0xDA4C5Cu,
            0xDA4C5Cu,
            0xBB6EC8u,
            0xD4AB75u,
            0xEEC756u,
            0x6D82C7u,
            0xD18651u,
            0xC79B51u,
            0xCBC470u,
            0xCEAE4Cu,
        };
    }
}
