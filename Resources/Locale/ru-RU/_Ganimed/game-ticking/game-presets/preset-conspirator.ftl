conspirator-objective-issuer = [color=#724F29]Заговор[/color]

conspirator-role-greeting = 
    Вы — заговорщик. 
    Вы знаете имена других заговорщиков, а также имеете имплант с радиоканалом, чтобы оставаться с ними на связи.
    Работайте сообща и не останавливайтесь ни перед чем, воплощая заговор в жизнь.

conspirator-count = { $count ->
        [one] Был { $count } заговорщик
        [few] Было { $count } заговорщика
       *[other] Было { $count } заговорщиков
    }:
conspirator-name-user = [color=white]{CAPITALIZE($name)}[/color] ([color=gray]{$username}[/color]) был заговорщиком.
conspirator-objective = Целью заговора являлось: [color=white]{$objective}[/color]

conspirator-identities = Список заговорщиков:
# Локализация в формате "{ $name } — один/одна/одни/одно из нас" или "{ $name }, { $job }" потребует изменений в коде
conspirator-name = { $name }
conspirator-radio-implant = Кооперируйтесь с другими заговорщиками, используя свой радиоимплант (:щ).

conspiracy-title = Заговорщики
conspiracy-description = Назревает заговор!
