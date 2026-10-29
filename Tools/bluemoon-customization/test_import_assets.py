import tempfile
import unittest
from pathlib import Path

from PIL import Image, PngImagePlugin

from import_assets import candidates, definitions, digest, frame_layout, frames_for, matrix_layers, read_dmi


class ImportTests(unittest.TestCase):
    def test_tail_front_uses_native_tail_layer_and_rear_stays_behind_body(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            file = root / 'tail.dmi'
            sheet = Image.new('RGBA', (32 * 8, 32), (255, 255, 255, 255))
            info = PngImagePlugin.PngInfo()
            info.add_text('Description', 'width = 32\nheight = 32\nstate = "m_tail_fox_BEHIND"\ndirs = 4\nframes = 1\nstate = "m_tail_fox_FRONT"\ndirs = 4\nframes = 1\n')
            sheet.save(file, format='PNG', pnginfo=info)
            data = {'name': '"Fox"', 'icon_state': '"fox"', 'icon': "'tail.dmi'",
                    '_source': 'tail.dm', 'mutant_part_string': '"tail"',
                    'relevant_layers': 'BODY_BEHIND_LAYER, BODY_FRONT_LAYER'}
            for category in ('tails', 'spines'):
                entry, = candidates(root, f'/datum/sprite_accessory/{category}/fox', data)
                self.assertEqual([(name, anchor) for name, _, _, anchor in entry['layers']],
                                 [('m_tail_fox_BEHIND_base', 'before:Chest'), ('m_tail_fox_FRONT_base', 'Tail')])
            read_dmi.cache_clear()

    def test_unsplit_tail_uses_native_tail_layer(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            file = root / 'tail.dmi'
            info = PngImagePlugin.PngInfo()
            info.add_text('Description', 'width = 32\nheight = 32\nstate = "vox_tail"\ndirs = 4\nframes = 1\n')
            Image.new('RGBA', (32 * 4, 32), (255, 255, 255, 255)).save(file, format='PNG', pnginfo=info)
            data = {'name': '"Vox"', 'icon_state': '"vox_tail"', 'icon': "'tail.dmi'", '_source': 'tail.dm'}
            entry, = candidates(root, '/datum/sprite_accessory/tails/vox', data)
            self.assertEqual(entry['layers'][0][3], 'Tail')
            read_dmi.cache_clear()

    def test_direction_major_export_skips_unnamed_frames(self):
        with tempfile.TemporaryDirectory() as folder:
            file = Path(folder) / 'test.dmi'
            sheet = Image.new('RGBA', (32 * 9, 32))
            for tile in range(9):
                sheet.paste((tile * 20, 0, 0, 255), (tile * 32, 0, (tile + 1) * 32, 32))
            info = PngImagePlugin.PngInfo()
            info.add_text('Description', 'width = 32\nheight = 32\nstate = ""\ndirs = 1\nframes = 1\nstate = "style"\ndirs = 4\nframes = 2\ndelay = 1,2\n')
            sheet.save(file, format='PNG', pnginfo=info)
            frames, delays = frames_for(file, 'style')
            self.assertEqual(delays, [0.1, 0.2])
            self.assertEqual([[f.getpixel((0, 0))[0] for f in direction] for direction in frames],
                             [[20, 100], [40, 120], [60, 140], [80, 160]])
            read_dmi.cache_clear()

    def test_duplicate_check_includes_north_and_ignores_transparent_rgb(self):
        first = [[Image.new('RGBA', (32, 32), (0, 255, 0, 0))] for _ in range(4)]
        second = [[Image.new('RGBA', (32, 32))] for _ in range(4)]
        self.assertEqual(digest([(first, [0.1])]), digest([(second, [0.1])]))
        second[1][0].putpixel((16, 4), (255, 255, 255, 255))
        self.assertNotEqual(digest([(first, [0.1])]), digest([(second, [0.1])]))

    def test_reshaded_hair_is_not_imported_as_another_style(self):
        first = [[Image.new('RGBA', (32, 32))]]
        second = [[Image.new('RGBA', (32, 32))]]
        first[0][0].putpixel((16, 4), (180, 180, 180, 255))
        second[0][0].putpixel((16, 4), (220, 220, 220, 255))
        self.assertNotEqual(digest([(first, [0.1])]), digest([(second, [0.1])]))
        self.assertEqual(digest([(first, [0.1])], True), digest([(second, [0.1])], True))

    def test_large_unshifted_overlay_preserves_byond_bottom_left_origin(self):
        with tempfile.TemporaryDirectory() as folder:
            file = Path(folder) / 'ears.dmi'
            image = Image.new('RGBA', (32, 64))
            image.putpixel((16, 48), (255, 0, 0, 255))
            info = PngImagePlugin.PngInfo()
            info.add_text('Description', 'width = 32\nheight = 64\nstate = "ears"\ndirs = 1\nframes = 1\n')
            image.save(file, format='PNG', pnginfo=info)
            frames, _ = frames_for(file, 'ears')
            self.assertEqual(frames[0][0].size, (32, 96))
            self.assertEqual(frames[0][0].getpixel((16, 48)), (255, 0, 0, 255))
            # In world coordinates the marker stays at the mob center, not 32px below it.
            self.assertEqual(frames[0][0].height / 2 - 48, 0)
            read_dmi.cache_clear()

    def test_large_overlays_keep_world_coordinates_in_every_direction_and_frame(self):
        for width, height, center, dimensions in [
            (32, 32, False, (32, 32)), (32, 48, False, (32, 32)),
            (32, 64, False, (32, 32)), (64, 32, False, (32, 32)),
            (45, 34, False, (32, 32)), (64, 32, True, (64, 32)),
            (96, 34, True, (96, 32)), (96, 34, True, (46, 34)),
            (45, 34, True, (46, 34)),
        ]:
            with self.subTest(size=(width, height), center=center, dimensions=dimensions), tempfile.TemporaryDirectory() as folder:
                file = Path(folder) / 'overlay.dmi'
                sheet = Image.new('RGBA', (width * 8, height))
                points = [(0, 0), (width - 1, 0), (0, height - 1), (width - 1, height - 1), (16, height - 16)]
                for tile in range(8):
                    for x, y in points:
                        sheet.putpixel((tile * width + x, y), (tile + 1, 150, 200, 255))
                info = PngImagePlugin.PngInfo()
                info.add_text('Description', f'width = {width}\nheight = {height}\nstate = "overlay"\ndirs = 4\nframes = 2\ndelay = 1,2\n')
                sheet.save(file, format='PNG', pnginfo=info)
                frames, _ = frames_for(file, 'overlay', center, dimensions)
                # Independent BYOND calculation: lower-left alignment plus center_image pixel offsets.
                pixel_x = -abs(dimensions[0] - 32) / 2 if center else 0
                pixel_y = -abs(dimensions[1] - 32) / 2 if center else 0
                for direction, images in enumerate(frames):
                    for frame, image in enumerate(images):
                        for x, y in points:
                            world_x = x - 16 + pixel_x
                            world_y = height - y - 16 + pixel_y
                            target = (int(world_x + image.width / 2), int(image.height / 2 - world_y))
                            self.assertEqual(image.getpixel(target), (frame * 4 + direction + 1, 150, 200, 255))
                read_dmi.cache_clear()

    def test_unshifted_tall_ears_and_wide_tails_pad_on_opposite_sides(self):
        self.assertEqual(frame_layout(32, 64), ((32, 96), (0, 0)))
        self.assertEqual(frame_layout(32, 48), ((32, 64), (0, 0)))
        self.assertEqual(frame_layout(64, 32), ((96, 32), (32, 0)))
        self.assertEqual(frame_layout(32, 32), ((32, 32), (0, 0)))

    def test_centered_overlay_uses_declared_dimensions_instead_of_sheet_size(self):
        self.assertEqual(frame_layout(96, 34, True, (96, 32)), ((96, 36), (0, 0)))
        self.assertEqual(frame_layout(96, 34, True, (46, 34)), ((146, 34), (50, 0)))

    def test_palette_regions_remain_separate_and_keep_black_outlines(self):
        source = Image.new('RGBA', (4, 1))
        source.putdata([(160, 0, 0, 255), (0, 120, 0, 128), (0, 0, 80, 255), (0, 0, 0, 255)])
        layers = dict(matrix_layers([[source]], 'MATRIX_ALL'))
        self.assertEqual(layers['primary'][0][0].getpixel((0, 0)), (160, 160, 160, 255))
        self.assertEqual(layers['secondary'][0][0].getpixel((1, 0)), (120, 120, 120, 128))
        self.assertEqual(layers['tertiary'][0][0].getpixel((2, 0)), (80, 80, 80, 255))
        self.assertEqual(layers['primary'][0][0].getpixel((3, 0)), (0, 0, 0, 255))
        self.assertEqual(layers['secondary'][0][0].getpixel((0, 0))[3], 0)

    def test_dm_inheritance_respects_include_order_and_comments(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / 'tgstation.dme').write_text('#include "a.dm"\n#include "b.dm"')
            (root / 'a.dm').write_text('/datum/sprite_accessory/hair\n\ticon = \'base.dmi\'\n/datum/sprite_accessory/hair/one\n\tname = "One" // trailing comment\n\ticon_state = "one"\n/*\n/datum/sprite_accessory/hair/fake\n\tname = "Fake"\n*/')
            (root / 'b.dm').write_text('/datum/sprite_accessory/hair/one\n    icon = \'override.dmi\'')
            items = dict(definitions(root))
            self.assertNotIn('/datum/sprite_accessory/hair/fake', items)
            self.assertEqual(items['/datum/sprite_accessory/hair/one']['name'], '"One"')
            self.assertEqual(items['/datum/sprite_accessory/hair/one']['icon'], "'override.dmi'")


if __name__ == '__main__':
    unittest.main()
