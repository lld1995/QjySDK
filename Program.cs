// See https://aka.ms/new-console-template for more information
using Common;
using QjySDK.Stg;

GlobalDef.Init();

{
    var sd = new ExtremeReversal("6cec087c826f4fcdbb0daa95dc5802c7");
    await sd.Run();
    
    Console.ReadLine();
    Console.ReadLine();
}

