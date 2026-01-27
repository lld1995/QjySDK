// See https://aka.ms/new-console-template for more information
using Common;
using QjySDK.Stg;

GlobalDef.Init();

{
    var sd = new MultiFactor("245044dd55a046829617593d4cda19a8");
    await sd.Run();
    
    Console.ReadLine();
    Console.ReadLine();
}

