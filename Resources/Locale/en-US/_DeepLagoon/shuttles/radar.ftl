shuttle-radar-signatures = { $count ->
    [one] { $count } signature
   *[other] { $count } signatures
}
