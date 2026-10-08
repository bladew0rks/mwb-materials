# MWB Mats

### Tool designed to mimic PBR with Source Engine's shader system.

For DX11 Garry's Mod, the "SurfaceGGX (DX11 GMod)" preset outputs real PBR materials using the SurfaceGGX shader instead.

![laugh](https://raw.githubusercontent.com/9lbw/mwb-materials/refs/heads/main/autoconverters.png)

If you need help click ![here](https://github.com/9lbw/mwb-materials/blob/main/help.md).

## Note for me

After cloning, fetch the SharpBcn submodule:

```sh
git submodule update --init
```

Debug build:

```sh
dotnet run --project mwb-materials
```

Release build (`win-x64` or `linux-x64`):

```sh
dotnet publish mwb-materials -c Release -r linux-x64
```
