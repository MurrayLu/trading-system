// The CLI prints Traditional Chinese; emit UTF-8 regardless of the host console code page.
Console.OutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

return await DemoCli.RunAsync(args);
