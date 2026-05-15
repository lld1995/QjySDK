// See https://aka.ms/new-console-template for more information
using Common;
using QjySDK.Stg;

GlobalDef.Init();

{
    var sd = new BollRsiShortReversion("8bbcebf535f94bc4804e0fca2a59bac8");
    await sd.Run();
    
    Console.ReadLine();
    Console.ReadLine();
}

