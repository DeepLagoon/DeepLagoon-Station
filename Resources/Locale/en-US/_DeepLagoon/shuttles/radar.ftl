shuttle-radar-autopilot-label = { $name } [Auto]

shuttle-radar-signatures = { $count ->
    [one] { $count } signature
   *[other] { $count } signatures
}
