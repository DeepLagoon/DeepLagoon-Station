<p align="center"><img alt="DeepLagoon Station" width="320" src=".github/readme-logo.png" /></p>

DeepLagoon Station это форк [Monolith-Station](https://github.com/Monolith-Station/Monolith), который, в свою очередь, является форком [Frontier Station 14](https://github.com/new-frontiers-14/frontier-station-14) и всё это добро работает на нашем скромном форке движка [Robust Toolbox](https://github.com/DeepLagoon/RobustToolbox), написанным на языке C#.

If you want to host or create content for DeepLagoon Station, this is the repo you need. It contains both RobustToolbox and the content pack for development of new content packs.

## Links

[Discord](https://discord.gg/uW7tgZeqVg) | [GitHub](https://github.com/DeepLagoon/DeepLagoon-Station) | [Boosty](https://boosty.to/iamasay)

## Contributing

Мы с радостью примем помощь от каждого, кто способен писать код, делать прототипы, рисовать спрайты, или же проектировать карты.
Для этого, вы можете посетить наш дискорд и написать соответствующую заявку для доступа к специальным чатам, но это не обязательно. Репозиторий открыт и вы можете сделать PR.

## Building

Refer to [the Space Wizards' guide](https://docs.spacestation14.com/en/general-development/setup/setting-up-a-development-environment.html) on setting up a development environment for general information, but keep in mind that Einstein Engines is not the same and many things may not apply.
We provide some scripts shown below to make the job easier.

### Build dependencies

> - Git
> - .NET SDK 10.0


### Windows

> 1. Clone this repository
> 2. Run `Scripts/bat/updateEngine.bat` in a terminal or in file explorer to download the engine
> 3. Run `Scripts/bat/buildAllDebug.bat` after making any changes to the source
> 4. Run `Scripts/bat/runQuickAll.bat` to launch the client and the server
> 5. Connect to localhost in the client and play

### Linux

> 1. Clone this repository
> 2. Run `Scripts/sh/updateEngine.sh` in a terminal to download the engine
> 3. Run `Scripts/sh/buildAllDebug.sh` after making any changes to the source
> 4. Run `Scripts/sh/runQuickAll.sh` to launch the client and the server
> 5. Connect to localhost in the client and play

### MacOS

> 1. Clone this repository
> 2. Run `Scripts/sh/updateEngine.sh` in a terminal to download the engine
> 3. Run `Scripts/sh/buildAllDebug.sh` after making any changes to the source
> 4. Run `Scripts/sh/runQuickAll.sh` to launch the client and the server
> 5. Connect to localhost in the client and play

## License

See the REUSE headers for detailed licensing information for each file for the specific licenses contributions are made under. The work as a whole is licensed under GNU Affero General Public License version 3.0.

By default, original code contributed to the Monolith codebase after 04d8ce483f638320d1b85a7aaacdf01442757363 is under Mozilla Public License version 2.0 with Exhibit B removed. See `LICENSE-MPL.txt`.

Content contributed to this repository after commit 2fca06eaba205ae6fe3aceb8ae2a0594f0effee0 is licensed under the GNU Affero General Public License version 3.0, unless otherwise stated. See `LICENSE-AGPLv3.txt`.

Content contributed to this repository before commit 2fca06eaba205ae6fe3aceb8ae2a0594f0effee0 is licensed under the MIT license, unless otherwise stated. See `LICENSE-MIT.txt`.


[2fca06eaba205ae6fe3aceb8ae2a0594f0effee0](https://github.com/new-frontiers-14/frontier-station-14/commit/2fca06eaba205ae6fe3aceb8ae2a0594f0effee0) was pushed on July 1, 2024 at 16:04 UTC

Most assets are licensed under [CC-BY-SA 3.0](https://creativecommons.org/licenses/by-sa/3.0/) unless stated otherwise. Assets have their license and the copyright in the metadata file. [Example](https://github.com/space-wizards/space-station-14/blob/master/Resources/Textures/Objects/Tools/crowbar.rsi/meta.json).

Note that some assets are licensed under the non-commercial [CC-BY-NC-SA 3.0](https://creativecommons.org/licenses/by-nc-sa/3.0/) or similar non-commercial licenses and will need to be removed if you wish to use this project commercially.
