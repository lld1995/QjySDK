// See https://aka.ms/new-console-template for more information
using Common;
using QjySDK.Stg;

GlobalDef.Init();

{
    var sd = new ChanLun("c3bb1894005a46b39a427a4f77cc595c");
    await sd.Run();
    
    Console.ReadLine();
    Console.ReadLine();
}

