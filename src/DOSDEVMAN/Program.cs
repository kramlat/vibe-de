using Vibe.Registry.Remote;

using var store = FhiveClient.OpenStore();
return Dosdevman.DosdevmanCommand.Run(args, Console.Out, Console.Error,
    new Vde.Shell.DosDevices(new Vde.Shell.DosDeviceStore(store.CurrentUser), () => new Vde.Shell.Computer().GetVolumes()), store.CurrentUser);
