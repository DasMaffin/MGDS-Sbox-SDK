# MGDS for s&box

The s&box SDK for **MGDS - Maffin's Gamedev Services**: sign players in with their s&box account,
save files, leaderboards, remote config, reports and push messages, from one component in your
first scene.

```csharp
await Mgds.WhenReady;                                     // signed in with s&box by itself
await Mgds.Saves.SaveAsync( "slot1", progress );
await Mgds.Leaderboards.SubmitScoreAsync( "high", 1234 );
float speed = Mgds.Config.GetFloat( "speed", 1f );
Mgds.Push.On( "MaintenanceWarning", m => banner.Show( m.Get<string>( "message" ) ) );
```

**Documentation: [dasmaffin.com/Mgds/Docs/sbox](https://www.dasmaffin.com/Mgds/Docs/sbox)**

## Install

Put this repository in your project's `Libraries` folder:

```
git submodule add https://github.com/DasMaffin/MGDS-Sbox-SDK.git Libraries/mgds
```

Then add the **MGDS Manager** component to a root GameObject in your first scene, paste your
project's id from [dasmaffin.com/Mgds](https://www.dasmaffin.com/Mgds), and add **s&box** as a
provider on the project's Authenticate page.

Everything stays inside s&box's sandbox: `Http`, `FileSystem.Data` and `Sandbox.Services.Auth`.
