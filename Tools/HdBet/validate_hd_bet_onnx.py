#!/usr/bin/env python3
"""
Validate exported hd_bet.onnx against official HD-BET on one volume.

Mirrors the Unity preprocess + sliding-window path (no TTA):
  crop-nonzero -> resample 1mm -> z-score -> sliding window -> restore.

Usage:
  python validate_hd_bet_onnx.py --input path/to/volume.nii.gz
  python validate_hd_bet_onnx.py --input path/to/volume.nrrd
"""

from __future__ import annotations

import argparse
import json
import tempfile
from pathlib import Path

import numpy as np
import SimpleITK as sitk


def _load_plans(plans_path: Path) -> dict:
    return json.loads(plans_path.read_text(encoding="utf-8"))


def _to_nifti_if_needed(input_path: Path, work_dir: Path) -> Path:
    if input_path.name.endswith(".nii") or input_path.name.endswith(".nii.gz"):
        return input_path
    image = sitk.ReadImage(str(input_path))
    out = work_dir / (input_path.stem + ".nii.gz")
    sitk.WriteImage(image, str(out))
    return out


def _crop_nonzero(image: sitk.Image) -> tuple[sitk.Image, tuple[int, int, int]]:
    arr = sitk.GetArrayFromImage(image)  # z,y,x
    mask = np.abs(arr) > 0
    if not np.any(mask):
        raise RuntimeError("Input volume has no non-zero voxels.")
    zs, ys, xs = np.where(mask)
    z0, z1 = int(zs.min()), int(zs.max())
    y0, y1 = int(ys.min()), int(ys.max())
    x0, x1 = int(xs.min()), int(xs.max())
    cropped = arr[z0 : z1 + 1, y0 : y1 + 1, x0 : x1 + 1]
    out = sitk.GetImageFromArray(cropped)
    new_origin = image.TransformIndexToPhysicalPoint([x0, y0, z0])
    out.SetSpacing(image.GetSpacing())
    out.SetDirection(image.GetDirection())
    out.SetOrigin(new_origin)
    return out, (x0, y0, z0)


def _resample_to_spacing(image: sitk.Image, spacing: tuple[float, float, float], is_label: bool) -> sitk.Image:
    src_spacing = np.array(image.GetSpacing(), dtype=np.float64)
    src_size = np.array(image.GetSize(), dtype=np.float64)
    tgt_spacing = np.array(spacing, dtype=np.float64)
    new_size = np.maximum(1, np.round(src_size * src_spacing / tgt_spacing)).astype(np.int32).tolist()

    rf = sitk.ResampleImageFilter()
    rf.SetOutputSpacing(tuple(float(v) for v in tgt_spacing))
    rf.SetSize([int(v) for v in new_size])
    rf.SetOutputOrigin(image.GetOrigin())
    rf.SetOutputDirection(image.GetDirection())
    rf.SetDefaultPixelValue(0)
    rf.SetInterpolator(sitk.sitkNearestNeighbor if is_label else sitk.sitkLinear)
    return rf.Execute(image)


def _zscore_nonzero(arr: np.ndarray) -> np.ndarray:
    out = arr.astype(np.float32, copy=True)
    mask = np.abs(out) > 0
    if not np.any(mask):
        return out
    values = out[mask]
    mean = float(values.mean())
    std = float(values.std())
    if std > 0:
        out[mask] = (values - mean) / std
    else:
        out[mask] = values - mean
    out[~mask] = 0
    return out


def _patch_starts(size: int, patch: int, stride: int) -> list[int]:
    if size <= patch:
        return [0]
    starts = list(range(0, size - patch, stride))
    starts.append(size - patch)
    return starts


def _gaussian_importance(shape: tuple[int, int, int]) -> np.ndarray:
    z, y, x = shape
    zz = (np.arange(z) - (z - 1) / 2.0) / (z / 8.0)
    yy = (np.arange(y) - (y - 1) / 2.0) / (y / 8.0)
    xx = (np.arange(x) - (x - 1) / 2.0) / (x / 8.0)
    gz, gy, gx = np.meshgrid(zz, yy, xx, indexing="ij")
    weight = np.exp(-0.5 * (gz * gz + gy * gy + gx * gx)).astype(np.float32)
    weight /= max(float(weight.max()), 1e-8)
    return np.maximum(weight, 1e-3)


def _predict_onnx(arr_zyx: np.ndarray, onnx_path: Path, patch: list[int], tile_step: float) -> np.ndarray:
    import onnxruntime as ort

    session = ort.InferenceSession(str(onnx_path), providers=["CPUExecutionProvider"])
    input_name = session.get_inputs()[0].name
    pz, py, px = patch
    sz, sy, sx = arr_zyx.shape
    stride = [max(1, int(p * tile_step)) for p in patch]
    starts_z = _patch_starts(sz, pz, stride[0])
    starts_y = _patch_starts(sy, py, stride[1])
    starts_x = _patch_starts(sx, px, stride[2])
    importance = _gaussian_importance((pz, py, px))

    foreground = np.zeros((sz, sy, sx), dtype=np.float32)
    weights = np.zeros((sz, sy, sx), dtype=np.float32)

    for oz in starts_z:
        for oy in starts_y:
            for ox in starts_x:
                patch_arr = np.zeros((1, 1, pz, py, px), dtype=np.float32)
                z1 = min(oz + pz, sz)
                y1 = min(oy + py, sy)
                x1 = min(ox + px, sx)
                patch_arr[0, 0, : z1 - oz, : y1 - oy, : x1 - ox] = arr_zyx[oz:z1, oy:y1, ox:x1]
                logits = session.run(None, {input_name: patch_arr})[0]  # (1,2,z,y,x)
                bg = logits[0, 0]
                fg = logits[0, 1]
                max_logit = np.maximum(bg, fg)
                prob = np.exp(fg - max_logit) / (np.exp(bg - max_logit) + np.exp(fg - max_logit))
                w = importance[: z1 - oz, : y1 - oy, : x1 - ox]
                foreground[oz:z1, oy:y1, ox:x1] += prob[: z1 - oz, : y1 - oy, : x1 - ox] * w
                weights[oz:z1, oy:y1, ox:x1] += w

    prob_vol = np.divide(foreground, weights, out=np.zeros_like(foreground), where=weights > 0)
    return (prob_vol >= 0.5).astype(np.uint8)


def _dice(a: np.ndarray, b: np.ndarray) -> float:
    a = a.astype(bool)
    b = b.astype(bool)
    inter = np.logical_and(a, b).sum()
    denom = a.sum() + b.sum()
    if denom == 0:
        return 1.0
    return float(2.0 * inter / denom)


def main() -> None:
    root = Path(__file__).resolve().parents[2]
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", type=Path, required=True, help="NIfTI or NRRD volume")
    parser.add_argument("--onnx", type=Path, default=root / "Assets" / "Models" / "HdBet" / "hd_bet.onnx")
    parser.add_argument("--plans", type=Path, default=root / "Assets" / "Models" / "HdBet" / "hd_bet_plans.json")
    parser.add_argument("--workdir", type=Path, default=None)
    args = parser.parse_args()

    plans = _load_plans(args.plans)
    patch = [int(v) for v in plans["patch_size"]]
    spacing = tuple(float(v) for v in plans["spacing"])
    tile_step = float(plans.get("tile_step_size", 0.5))

    with tempfile.TemporaryDirectory() as tmp:
        work = Path(args.workdir) if args.workdir else Path(tmp)
        work.mkdir(parents=True, exist_ok=True)
        nifti_path = _to_nifti_if_needed(args.input.resolve(), work)

        # Official HD-BET reference
        from HD_BET.checkpoint_download import maybe_download_parameters
        from HD_BET.hd_bet_prediction import get_hdbet_predictor, hdbet_predict
        import torch

        maybe_download_parameters()
        predictor = get_hdbet_predictor(use_tta=False, device=torch.device("cpu"), verbose=False)

        # hd_bet_prediction joins paths with cwd; run from workdir with simple names.
        import os

        nifti_local = work / "input.nii.gz"
        if Path(nifti_path).resolve() != nifti_local.resolve():
            sitk.WriteImage(sitk.ReadImage(str(nifti_path)), str(nifti_local))

        prev_cwd = Path.cwd()
        os.chdir(work)
        try:
            hdbet_predict(
                "input.nii.gz",
                "ref_stripped.nii.gz",
                predictor,
                keep_brain_mask=True,
                compute_brain_extracted_image=False,
            )
        finally:
            os.chdir(prev_cwd)

        candidate_masks = [
            work / "ref_stripped_bet.nii.gz",
            work / "input_bet.nii.gz",
        ]
        ref_mask_path = next((p for p in candidate_masks if p.exists()), None)
        if ref_mask_path is None:
            found = list(work.glob("*bet*.nii.gz"))
            if not found:
                raise FileNotFoundError(f"Could not locate HD-BET mask in {work}")
            ref_mask_path = found[0]

        ref_img = sitk.ReadImage(str(ref_mask_path))
        ref_arr = sitk.GetArrayFromImage(ref_img) > 0

        # ONNX mirrored pipeline
        src = sitk.ReadImage(str(nifti_path))
        cropped, _ = _crop_nonzero(src)
        resampled = _resample_to_spacing(cropped, spacing, is_label=False)
        arr = _zscore_nonzero(sitk.GetArrayFromImage(resampled))
        pred_arr = _predict_onnx(arr, args.onnx.resolve(), patch, tile_step)

        pred_img = sitk.GetImageFromArray(pred_arr)
        pred_img.CopyInformation(resampled)
        # restore to original geometry
        restored = sitk.Resample(
            pred_img,
            src,
            sitk.Transform(),
            sitk.sitkNearestNeighbor,
            0,
            sitk.sitkUInt8,
        )
        pred_full = sitk.GetArrayFromImage(restored) > 0

        # Align shapes if needed
        if pred_full.shape != ref_arr.shape:
            raise RuntimeError(f"Shape mismatch onnx={pred_full.shape} ref={ref_arr.shape}")

        dice = _dice(pred_full, ref_arr)
        print(f"input={args.input}")
        print(f"onnx={args.onnx}")
        print(f"ref_mask={ref_mask_path}")
        print(f"ref_brain_voxels={int(ref_arr.sum())}")
        print(f"onnx_brain_voxels={int(pred_full.sum())}")
        print(f"dice={dice:.4f}")
        if dice < 0.85:
            raise SystemExit(f"Validation dice too low: {dice:.4f}")
        print("OK")


if __name__ == "__main__":
    main()
