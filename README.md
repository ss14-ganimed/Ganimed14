<p align="center">
  <img alt="Space Station 14" width="650" src="https://github.com/ss14-ganimed/Ganimed14/blob/master/Resources/Textures/_Ganimed/Logo/logo-ganimed.png" />
</p>

<div align="center">

  [![Discord](https://img.shields.io/discord/1203769510599856138?label=Join%20our%20Discord&logo=discord&logoColor=white&style=for-the-badge)](https://discord.gg/CdJJmU3fGV)
  [![Wiki](https://img.shields.io/badge/Wiki-Explore%20Our%20Wiki-blue?style=for-the-badge)](https://station-enterprise.space/)
  [![Steam](https://img.shields.io/badge/Steam-Play%20on%20Steam-blue?style=for-the-badge)](https://store.steampowered.com/app/1255460/Space_Station_14/)
  [![Client](https://img.shields.io/badge/Download-Client-blue?style=for-the-badge)](https://spacestation14.io/about/nightlies/)
  [![GitHub](https://img.shields.io/github/stars/ss14-ganimed/ganimed14?style=for-the-badge&logo=github)](https://github.com/ss14-ganimed/Ganimed14)

</div>

<p align="center">
  <img src="https://img.shields.io/github/commit-activity/y/ss14-ganimed/ganimed14?style=flat-square" alt="GitHub commit activity">
  <img src="https://img.shields.io/github/issues/ss14-ganimed/ganimed14?style=flat-square" alt="GitHub Issues">
  <img src="https://img.shields.io/github/issues-pr-closed/ss14-ganimed/ganimed14?style=flat-square" alt="GitHub Closed PRs">
</p>

---

## О проекте

Это репозиторий исходного кода проекта русскоязычного сервера **Enterprise**, форка [Space Station 14](https://github.com/space-wizards/space-station-14), основанного на билде проекта [Время Приключений](https://github.com/AdventureTimeSS14/space_station_ADT).

**Space Station 14** — это захватывающая ролевая игра, вдохновлённая культовой Space Station 13.
Погрузитесь в атмосферу научной космической станции корпорации Nanotrasen, где каждое ваше действие может привести к неожиданным последствиям.
- Уникальный геймплей, поддерживаемый целым рядом сообществ.
- Интенсивное взаимодействие игроков в замкнутом пространстве станции.
- Постоянное развитие благодаря движку [Robust Toolbox](https://github.com/space-wizards/RobustToolbox), написанному на C#.

**Enterprise** (ранее Ганимед) — это проект небольшого сообщества энтузиастов по игре Space Station 14, что стремится поддерживать повышенный уровень ролевого отыгрыша благодаря нововведениям во внутриигровые инструменты и механики, фокусируясь на неожиданном веселье, интересном повествовании, проработанности игровой вселенной, сюжете и атмосферности. Здесь каждый раунд становится интересной историей.

---

<p align="center">
  <b>✨ Активность проекта</b>
</p>
<p align="center">
  <i>Следите за динамикой проекта и вовлечённостью сообщества:</i>
</p>

<div align="center">

![Активность PR](https://repobeats.axiom.co/api/embed/7840b3c2c32e27c46c75e041f581711f91b53f36.svg "Repobeats analytics image")

</div>

---

## Участники проекта

Этот проект невозможен без усилий нашего сообщества. Вот те, кто внёс наибольший вклад:

[![Участники](https://contrib.rocks/image?repo=ss14-ganimed/ganimed14)](https://github.com/ss14-ganimed/ganimed14/graphs/contributors)

---

## Контрибьют

Мы рады принять вклад от любого человека. Заходите в [Discord](https://discord.gg/CdJJmU3fGV), если хотите помочь. У нас есть [список проблем](https://github.com/ss14-ganimed/Ganimed14/issues), которые нужно решить, и любой может за них взяться. Не бойтесь просить о помощи!
Только убедитесь, что ваши изменения и PRы соответствуют [руководству по контрибьюту](https://docs.spacestation14.com/en/general-development/codebase-info/pull-request-guidelines.html).

Любые новые механики, системы, компоненты, прототипы и прочие уникальные нововведения для сервера должны находиться в подпапке `/_Ganimed/` папок `/Resources/*/` или `/Content.*/`. Структура файлов и папок в подпапке `/_Ganimed/` должна приблизительно повторять основную структуру файлов.

К каждому изменению файлов формата `.cs` и `.yml` вне папки `/_Ganimed/` (т.е. уже существующих файлов репозитория-родителя) необходимо добавлять соответствующие комментарии `Ganimed-Edit` о вносимых изменениях, дополняя комментарий примечанием о том, что именно было изменено, желательно на английском языке.

---

## Сборка

1. Склонируйте этот репозиторий локально с помощью команды `git clone https://github.com/ss14-ganimed/Ganimed14.git`.
2. Выберите папку с локальным репозиторием командой `cd Ganimed14` и запустите `RUN_THIS.py` для инициализации подмодулей и скачивания движка.
3. Скомпилируйте проект с помощью команды `dotnet build`.

[Более подробная инструкция по запуску проекта.](https://docs.spacestation14.com/en/general-development/setup.html)

---

## Документация/Вики

На [официальном сайте с документацией](https://docs.spacestation14.io/) имеется вся необходимая информация о контенте SS14, движке и дизайне игры, а также много информации для начинающих разработчиков.

На [сайте проекта Enterprise](https://station-enterprise.space/) можно найти подробную информацию об этом сервере.

---

## Лицензия и авторские права

Содержимое (код) [этого репозитория/проекта (Ganimed14)](https://github.com/ss14-ganimed/Ganimed14) (включая модификации, внесённые контрибьюторами этого проекта, источником которых является этот репозиторий) лицензировано/распространяется под лицензией [**GNU Affero General Public License версии 3.0 или более поздней**](https://github.com/ss14-ganimed/Ganimed14/blob/master/LICENSES/AGPL-3.0-or-later.txt), вступившей в силу с `2 августа 2024 08:50:00 UTC`, начиная с коммита [`8bc6168808c42a5a1b026b938a852d59aab2ee50`](https://github.com/ss14-ganimed/Ganimed14/commit/8bc6168808c42a5a1b026b938a852d59aab2ee50) включительно, если не указано иное.

Соответственно, исходный код этого репозитория **до** коммита `8bc6168808c42a5a1b026b938a852d59aab2ee50` (`2 августа 2024 08:50:00 UTC`), а также исходный код игры [Space Station 14](https://github.com/space-wizards/space-station-14), лицензирован/распространяется под лицензией [**MIT**](https://github.com/ss14-ganimed/Ganimed14/blob/master/LICENSES/MIT.txt), если не указано иное.

Условия лицензий на исходный код, источником которого являются иные авторы/репозитории/проекты должны соблюдаться наравне с условиями лицензии этого репозитория/проекта (Ganimed14).

Некоторые файлы содержат заголовки-комментарии в соответствии со [спецификацией REUSE](https://reuse.software/) или отдельные файлы (`license`) с информацией о лицензии, авторском праве и условиях повторного использования.

Большинство ассетов лицензированы под [CC-BY-SA 3.0](https://creativecommons.org/licenses/by-sa/3.0/), если не указано иное. Ассеты имеют свою лицензию и информацию об авторском праве в файле метаданных ([`meta`](https://docs.spacestation14.com/en/specifications/robust-station-image.html) или [`attributions`](https://docs.spacestation14.com/en/specifications/robust-generic-attribution.html)). [Пример](https://github.com/ss14-ganimed/Ganimed14/blob/master/Resources/Textures/Objects/Tools/crowbar.rsi/meta.json).

Обратите внимание, что некоторые ассеты лицензированы на некоммерческой основе [CC-BY-NC-SA 3.0](https://creativecommons.org/licenses/by-nc-sa/3.0/) или аналогичной некоммерческой лицензией, и их необходимо удалить, если вы хотите использовать этот проект в коммерческих целях.

Организация-владелец проекта не претендует на право собственности на работы (включая код, модификации, ассеты или ресурсы), созданные иными авторами/репозиториями/проектами/третьими сторонами или оригинальными разработчиками Space Station. Оригинальные авторы работ сохраняют за собой все авторские права на работы собственного авторства.

Copyright (c) 2023-2026 Enterprise
