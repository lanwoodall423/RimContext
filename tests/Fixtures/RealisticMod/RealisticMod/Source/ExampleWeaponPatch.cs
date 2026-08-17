using HarmonyLib;

namespace RealisticMod;

[HarmonyPatch(typeof(ExampleWeapon), "TickWeapon")]
public static class ExampleWeaponPatch
{
    [HarmonyPostfix]
    public static void Postfix(ExampleWeapon __instance)
    {
    }
}
