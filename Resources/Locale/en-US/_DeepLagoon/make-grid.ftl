# Make grid
cmd-makegrid-desc = Creates a new grid fully covered with plating, centered on your entity.
cmd-makegrid-help = Usage: {$command} <width> <height>
    Both sizes are whole numbers from 1 to the maximum, in tiles. Odd size: you stand over the center tile. Even size: the center is on the line between the two center tiles nearest to you. Refuses to overlap tiles of another grid.
cmd-makegrid-hint-width = <width, 1..{$max}>
cmd-makegrid-hint-height = <height, 1..{$max}>
cmd-makegrid-wrong-args = Expected exactly two arguments.
cmd-makegrid-bad-size = Width and height must be whole numbers from 1 to {$max}.
cmd-makegrid-no-player = This command can only be run by a player, not from the server console.
cmd-makegrid-no-entity = You must be in an entity (not in the lobby) to run this command.
cmd-makegrid-no-map = Your entity is not on a map.
cmd-makegrid-no-tile = Tile "{$tile}" was not found.
cmd-makegrid-overlap = The area overlaps tiles of another grid. Move away from it or use a smaller size.
cmd-makegrid-done = Created grid {$grid}, {$width}x{$height}, origin at ({$x}, {$y}).
