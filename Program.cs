// See https://aka.ms/new-console-template for more information
using Common;
using QjySDK.Stg;

GlobalDef.Init();

{
    var sd = new ChanLun("3acd2ec2870243cb850012c1b1bfc74f");
    await sd.Run();
    
    Console.ReadLine();
    Console.ReadLine();
}

