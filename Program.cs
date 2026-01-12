// See https://aka.ms/new-console-template for more information
using Common;
using QjySDK.Stg;

GlobalDef.Init();

{
    var sd = new ChanLun("5a8597c4adbb4bb8ab906211db0bc7fd");
    await sd.Run();
    
    Console.ReadLine();
    Console.ReadLine();
}

