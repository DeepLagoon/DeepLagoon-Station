shuttle-radar-signatures = { $count ->
    [one] { $count } сигнатура
    [few] { $count } сигнатуры
   *[other] { $count } сигнатур
}
