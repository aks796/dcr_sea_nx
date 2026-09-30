// Features.cs -- what the mod adds, installed in this order at Init.
namespace DcrMod
{
    public static class Features
    {
        public static void Install()
        {
            Ui.Install();
            FastBundles.Install();
            Preload.Install();
            Online.Install();
            MenuPad.Install();
            Profiles.Install();
            LocalMP.Install();
            Characters.Install();
            Sorcerer.Install();
            TestDriver.Install();
        }
    }
}
