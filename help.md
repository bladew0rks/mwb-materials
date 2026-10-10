# How to setup textures

Make a folder and name it like you want the vmt to be named:

![help1](https://cdn.discordapp.com/attachments/1137688979743981648/1137699742458056754/image.png)

In that folder you're going to place a few textures:

- albedo (**_rgb**, **_rgbm**, **_s~**, or **_c**)
- normal (**_n**)
- roughness/gloss (**_r**/**_g**)
- metalness (**_m** or **_alpha**)
- ambient occlusion (**_o** or **_ao**)
- emissive/glow (**_e**)
- alphatest opacity (**_t**)
- translucent opacity (**_opacity**)
- packed ORM/RMA/MRAO (**_orm**, **_rma**, **_mrao**)
- CoD NOG/NG packed normal/gloss/occlusion (**packed_ng**, **packed_nog**, **_nog**, **_n&**, **_g~**)

![help2](https://cdn.discordapp.com/attachments/1137688979743981648/1137690447884603482/image.png)

**Remember to rename the images by adding the texture type at the end of their names (refer to list above).**

Emissive textures should be mostly black, with only the glowing parts colored. Darker colors glow less, and pure black will not glow.

Opacity textures use white for opaque and black for transparent. Use _t for `$alphatest` or _opacity for `$translucent`. When an opacity mask is present, the basetexture alpha carries opacity instead of metalness, so `$blendtintbybasealpha` is disabled. The Alphatest slider controls the `$alphatestreference` threshold (0.0–1.0, default 0.5).

After that, you can press the Open Folders button and select your folder.


# Envmaps

Tool comes with a few envmaps that it uses to generate accurate roughness.  
By default it generates an envmap texture for every vmt, but if you want you can specify a folder for shared usage (make sure it's inside your game's materials folder).

![help3](https://cdn.discordapp.com/attachments/1137688979743981648/1137695819244511252/image.png)

You can find the textures needed for this to work in the envmaps folder of the tool.

![help4](https://cdn.discordapp.com/attachments/1137688979743981648/1137695457137664110/image.png)

**Do not change the names!**


# Batch

If you have a group of folders you can select the root folder instead when pressing the Open Folders button.

![help5](https://cdn.discordapp.com/attachments/1137688979743981648/1137700302947102720/image.png)

![help6](https://cdn.discordapp.com/attachments/1137688979743981648/1137700568886947970/image.png)

If you need, you can also let it create the same folders it found while generating in the output destination.

![help7](https://cdn.discordapp.com/attachments/1137688979743981648/1137701639189434368/image.png)

![help8](https://cdn.discordapp.com/attachments/1137688979743981648/1137702458978742312/image.png)

## Converting phong materials

The **Phong to SurfaceGGX** tab turns existing VertexLitGeneric phong materials into PBR ones. Switch to it, press **Open materials folder** and pick a `materials` folder (or any folder inside one) that holds the VMTs and their VTFs. Texture paths are resolved from the `materials` folder, `patch` VMTs are followed through their include, and VMTs that share the same textures share one set of output textures.

- Metalness comes from the green channel of `$phongexponenttexture` (the `$phongalbedotint` mask).
- Gloss comes from the red channel (the phong exponent), varied by the normal map alpha (or base alpha with `$basemapalphaphongmask`) so wear and scratches stay visible. **Gloss variation** sets how strong that is.
- Metal is brightened in the albedo, since phong lit it with `$phongalbedoboost` on top of a dark diffuse. **Metal boost** sets how much, **Metal max** caps how bright it can get.
- `$alphatest` and `$translucent` carry over through the base texture alpha, and `$surfaceprop`, `$nocull` and `$alphatestreference` are copied into the new VMTs.

If the phong materials were made with MWB Mats (any phong preset), tick **Made with MWB Mats**. The tool then reads the albedo, gloss and metalness back exactly as it wrote them instead of estimating them, and the sliders are not used. The log suggests it when a VMT looks like MWB Mats output.

Tick **Generate folders in destination** to recreate the material folder structure in the output destination, so the result can override the original addon. If materials from different addons include each other's VMTs or textures, put them into one `materials` folder first.

# FAQ

> Which textures are required?

All of the textures are optional. The tool will work with whatever it finds in the textures folder.


> Tool won't load my textures. What's going on?

Make sure the textures' file type is png, jpg, bmp, gif, psd, a .tga file, or a .dds file (DXT1-5 / BC1-3, BC4, BC5, BC7, and uncompressed RGB/RGBA). HDR/float DDS formats (BC6H, R16F, R32F) are not supported.


> I don't have a glossiness mask. Can I use roughness?

Yes, the tool will invert your roughness texture automatically. Just make sure your file ends with **_r**.


> Can I use a specular map instead of metalness?

No.


> Do I have to credit you?

That'd be nice but no.

# Known limitations

- For best effect, you should split the mesh into separate vmts for metallic parts.  
- Phong albedo boost is only available in CS:GO and Garry's Mod, you can use phong boost in other games.  
- Manual adjustments of the vmts are required sometimes.
- Completely glossy materials aren't possible with a phong exponent texture (max is 150 - way too diffuse).
- Note that every game/engine has its own PBR implementation; this means results may differ from your reference pictures. The tool tends to be more accurate when assets are rendered using Substance, Sketchfab, Maya or 3ds Max.
