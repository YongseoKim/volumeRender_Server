# HD-BET ONNX export for Unity Sentis

Official HD-BET ships PyTorch / nnU-Net checkpoints only. This script exports the
network backbone to `Assets/Models/HdBet/hd_bet.onnx` plus `hd_bet_plans.json`.

## Setup

```bash
# From repo root (Windows example with Anaconda Python)
python -m venv Tools/HdBet/.venv
Tools\HdBet\.venv\Scripts\activate
pip install --upgrade pip
pip install torch hd-bet onnx onnxscript onnxruntime
```

## Export

```bash
python Tools/HdBet/export_hd_bet_onnx.py
```

Weights download automatically to `~/hd-bet_params/release_2.0.0` on first run.
Outputs:

- `Assets/Models/HdBet/hd_bet.onnx`
- `Assets/Models/HdBet/hd_bet_plans.json` (`patch_size=[96,192,192]`, spacing 1 mm)

## Validate (ONNX pipeline vs official HD-BET)

```bash
python Tools/HdBet/validate_hd_bet_onnx.py --input Assets/StreamingAssets/Sample/BraTS2021_00000_flair.nrrd
```

Expect Dice roughly ≥ 0.85 (reference run ~0.96 without TTA).

## Unity

Assign `HdBetSentisInference` on a GameObject and wire it to
`SimpleITKVolumeLoader.segmentationInference`. The viewer scene uses
`HdBetSegmentationModel` with colormap `HdBetSegmentationColorMap.txt`.

## Notes

- ONNX contains the backbone only. Unity reimplements crop / resample / z-score /
  sliding-window / restore in `HdBetSentisInference`.
- HD-BET pretrained weights are typically **CC BY-NC 4.0**. Do not redistribute
  commercially without checking the Zenodo / upstream license.
