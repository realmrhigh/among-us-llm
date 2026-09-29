using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace Impostor.Server.LlmBots.Maps
{
    /// <summary>
    ///     A hand made waypoint graph of The Skeld. Coordinates are Among Us world units and follow the door,
    ///     vent and console positions that ship with Impostor. It is an approximation: hallways are straight
    ///     segments between doors, so bots may cut a corner where the real hallway bends.
    /// </summary>
    internal static class SkeldGraph
    {
        // node <id> <room> <x> <y>       edge <a> <b> [<b> ...]
        private const string Definition = @"
# hub <room> <node>: where a bot heads when it just wants to be 'in' that room
hub Cafeteria CAF_S
hub MedBay MED_C
hub UpperEngine UE_C
hub LowerEngine LE_C
hub Security SEC_C
hub Reactor RX_C
hub Electrical EL_C
hub Storage STO_C
hub Admin ADM_C
hub Weapons WPN_C
hub LifeSupp O2_C
hub Nav NAV_1
hub Shields SHD_C
hub Comms COM_C

# ---- Cafeteria ring around the table
node CAF_E   Cafeteria   3.6  1.3
node CAF_NE  Cafeteria   2.6  4.4
node CAF_N   Cafeteria  -0.7  5.4
node CAF_NW  Cafeteria  -4.0  4.6
node CAF_W   Cafeteria  -4.9  1.3
node CAF_SW  Cafeteria  -4.0 -2.2
node CAF_S   Cafeteria  -0.7 -3.6
node CAF_SE  Cafeteria   2.6 -2.2
node CAF_WIRES Cafeteria -5.3 5.2
node CAF_VENT  Cafeteria  4.3 -0.3
node CAF_BUTTON Cafeteria -0.7 2.4
edge CAF_E CAF_NE CAF_SE CAF_VENT
edge CAF_NE CAF_N
edge CAF_N CAF_NW CAF_BUTTON
edge CAF_NW CAF_W CAF_WIRES
edge CAF_W CAF_SW
edge CAF_SW CAF_S
edge CAF_S CAF_SE
edge CAF_SE CAF_VENT

# ---- Doors of the Cafeteria
node D_CAF_W Cafeteria  -6.4  1.3
node D_CAF_E Cafeteria   5.1  1.3
node D_CAF_S Cafeteria  -0.7 -5.0
edge CAF_W D_CAF_W
edge CAF_E D_CAF_E
edge CAF_S D_CAF_S

# ---- West hallway, MedBay, Upper Engine
node WH_1    WestHall    -8.0  1.3
node WH_MED  WestHall    -9.1  1.3
node WH_2    WestHall   -12.0  1.3
node D_UE_E  UpperEngine -14.7 1.3
edge D_CAF_W WH_1
edge WH_1 WH_MED
edge WH_MED WH_2
edge WH_2 D_UE_E

node MED_D      MedBay -9.1 -0.4
node MED_C      MedBay -8.3 -2.6
node MED_SCAN   MedBay -7.3 -5.2
node MED_SAMPLE MedBay -6.1 -4.3
node MED_VENT   MedBay -10.6 -4.2
edge WH_MED MED_D
edge MED_D MED_C
edge MED_C MED_SCAN MED_SAMPLE MED_VENT
edge MED_SCAN MED_SAMPLE

node UE_C     UpperEngine -16.9  0.9
node UE_ALIGN UpperEngine -19.2 -0.4
node UE_VENT  UpperEngine -15.3  2.5
node UE_S     UpperEngine -16.9 -2.1
edge D_UE_E UE_C
edge UE_C UE_ALIGN UE_VENT UE_S

# ---- Vertical hallway between the engines (Security, Reactor)
node VC_1   EngineHall -16.9 -3.6
node VC_SEC EngineHall -16.9 -5.1
node VC_2   EngineHall -16.9 -7.2
node LE_N   LowerEngine -16.9 -9.0
edge UE_S VC_1
edge VC_1 VC_SEC
edge VC_SEC VC_2
edge VC_2 LE_N

node SEC_WIRES Security -15.6 -4.6
node SEC_D     Security -14.7 -5.1
node SEC_C     Security -13.5 -5.6
node SEC_VENT  Security -12.5 -6.9
edge VC_SEC SEC_WIRES
edge SEC_WIRES SEC_D
edge SEC_D SEC_C
edge SEC_C SEC_VENT

node RX_E     Reactor -19.0 -5.1
node RX_C     Reactor -21.0 -5.0
node RX_START Reactor -21.8 -5.6
node RX_MANI  Reactor -22.5 -2.5
node RX_V1    Reactor -20.8 -7.0
node RX_V2    Reactor -21.9 -3.1
edge VC_SEC RX_E
edge RX_E RX_C
edge RX_C RX_START RX_MANI RX_V1
edge RX_MANI RX_V2

# ---- Lower Engine and the bottom hallway
node LE_C     LowerEngine -17.2 -11.0
node LE_ALIGN LowerEngine -19.2 -12.6
node LE_VENT  LowerEngine -15.3 -13.7
node LE_E     LowerEngine -14.7 -11.5
edge LE_N LE_C
edge LE_C LE_ALIGN LE_VENT LE_E

node BH_1    BottomHall -12.6 -11.9
node BH_2    BottomHall -12.2 -14.3
node BH_ELEC BottomHall  -9.5 -14.3
node BH_3    BottomHall  -7.4 -14.3
edge LE_E BH_1
edge BH_1 BH_2
edge BH_2 BH_ELEC
edge BH_ELEC BH_3
edge BH_3 D_STO_W

# ---- Electrical
node EL_D      Electrical  -9.5 -13.4
node EL_C      Electrical  -8.4 -10.5
node EL_DIVERT Electrical  -9.0  -7.3
node EL_WIRES  Electrical  -7.7  -7.7
node EL_CAL    Electrical  -5.9  -7.5
node EL_VENT   Electrical  -9.8  -8.0
edge BH_ELEC EL_D
edge EL_D EL_C
edge EL_C EL_DIVERT EL_WIRES EL_CAL
edge EL_DIVERT EL_VENT

# ---- Storage
node D_STO_W Storage -5.3 -14.3
node STO_W   Storage -3.9 -13.6
node STO_C   Storage -1.4 -11.4
node STO_N   Storage -0.7  -9.4
node STO_WIRES Storage -1.9 -8.7
node D_STO_N Storage -0.7  -8.6
node D_STO_E Storage  1.1 -12.0
edge D_STO_W STO_W
edge STO_W STO_C
edge STO_C STO_N D_STO_E
edge STO_N STO_WIRES D_STO_N

# ---- Hallway from the Cafeteria south to Storage, Admin off to the east
node SH_1 SouthHall -0.7 -6.5
node SH_2 SouthHall -0.7 -7.6
edge D_CAF_S SH_1
edge SH_1 SH_2
edge SH_2 D_STO_N

node ADM_W      Admin  0.8 -6.6
node ADM_WIRES  Admin  1.4 -6.4
node ADM_UPLOAD Admin  2.5 -6.3
node ADM_C      Admin  3.6 -7.8
node ADM_SWIPE  Admin  5.6 -8.6
node ADM_VENT   Admin  2.5 -10.0
edge SH_1 ADM_W
edge ADM_W ADM_WIRES
edge ADM_WIRES ADM_UPLOAD
edge ADM_UPLOAD ADM_C
edge ADM_C ADM_SWIPE ADM_VENT

# ---- East hallway, Weapons, O2, the big Y, Navigation
node EH_1     EastHall  6.8  1.3
node WPN_D    Weapons  7.9  2.0
node WPN_C    Weapons  9.6  2.4
node WPN_AST  Weapons  9.1  1.8
node WPN_VENT Weapons  8.8  3.3
edge D_CAF_E EH_1
edge EH_1 WPN_D
edge WPN_D WPN_C
edge WPN_C WPN_AST WPN_VENT

node EH_2  EastHall   7.0 -0.6
node O2_D  LifeSupp  6.6 -2.2
node O2_C  LifeSupp  5.9 -3.0
edge EH_1 EH_2
edge EH_2 O2_D
edge O2_D O2_C

node Y_N   BigYHall  9.4 -1.5
node Y_NAV BigYHall  9.4 -4.6
node Y_MID BigYHall  9.4 -6.4
node Y_S   BigYHall  9.4 -10.0
node Y_S2  BigYHall  9.4 -12.6
edge EH_2 Y_N
edge Y_N Y_NAV
edge Y_NAV Y_MID
edge Y_MID Y_S
edge Y_S Y_S2

node NAV_D     Nav 11.6 -4.6
node NAV_1     Nav 14.5 -4.6
node NAV_WIRES Nav 14.5 -3.8
node NAV_STEER Nav 18.7 -4.7
node NAV_V1    Nav 16.0 -3.2
node NAV_V2    Nav 16.0 -6.4
edge Y_NAV NAV_D
edge NAV_D NAV_1
edge NAV_1 NAV_WIRES NAV_STEER NAV_V1 NAV_V2

# ---- Shields and Communications, reached from Storage over the southern hallway
node SE_1   StorageEastHall  3.0 -12.0
node SE_2   StorageEastHall  6.0 -12.7
node COM_D  Comms    4.0 -13.8
node COM_C  Comms    3.8 -15.4
node SHD_D  Shields  8.9 -13.3
node SHD_C  Shields  7.8 -14.0
node SHD_VENT Shields 9.5 -14.3
edge D_STO_E SE_1
edge SE_1 SE_2 COM_D
edge SE_2 Y_S2
edge COM_D COM_C
edge Y_S2 SHD_D
edge SHD_D SHD_C
edge SHD_C SHD_VENT
";

        public static NavGraph Build(bool mirrorX, out Dictionary<string, string> hubs)
        {
            var graph = new NavGraph();
            hubs = new Dictionary<string, string>();
            var lines = Definition.Split('\n');

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts[0] == "node")
                {
                    var x = float.Parse(parts[3], CultureInfo.InvariantCulture);
                    var y = float.Parse(parts[4], CultureInfo.InvariantCulture);
                    graph.Add(parts[1], parts[2], new Vector2(mirrorX ? -x : x, y));
                }
                else if (parts[0] == "hub")
                {
                    hubs[parts[1]] = parts[2];
                }
            }

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.StartsWith("edge ", StringComparison.Ordinal))
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    for (var i = 2; i < parts.Length; i++)
                    {
                        graph.Link(parts[1], parts[i]);
                    }
                }
            }

            return graph;
        }
    }
}
