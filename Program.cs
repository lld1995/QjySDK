// See https://aka.ms/new-console-template for more information
using Common;
using QjySDK;

GlobalDef.Init();

{
    var sd = new StgDemo("06bafb4768aa445fa0079df2eeb9a050");
    await sd.Run();
    
    Console.ReadLine();
    Console.ReadLine();
}

