import tempfile
import unittest
from pathlib import Path

from PIL import Image, PngImagePlugin

from import_assets import definitions, digest, frames_for, matrix_layers, read_dmi


class ImportTests(unittest.TestCase):
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
            self.assertEqual(frames[0][0].getpixel((16, 80)), (255, 0, 0, 255))
            read_dmi.cache_clear()

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
