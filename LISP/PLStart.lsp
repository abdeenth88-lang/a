;;; AB Bridges - PLS : shows where a polyline (line, arc, spline, alignment-like curve) starts and its direction.
;;; Temporary marks only (grdraw): they are NOT in the drawing and they disappear on zoom / pan / regen.
;;;   green circle + big green arrow = the start,  red arrows = the direction,  red square = the end.
;;; Load: APPLOAD -> PLStart.lsp (or add it to the Startup Suite), then type PLS.

(vl-load-com)

;; an arrow head (and a short tail) at pt (WCS), pointing along ang (UCS), size sz, colour col
(defun ab:pls-arrow (pt ang sz col / tip)
  (setq tip (trans pt 0 1))
  (grdraw (polar tip (+ ang pi) (* sz 1.4)) tip col)
  (grdraw tip (polar tip (+ ang (* 0.85 pi)) sz) col)
  (grdraw tip (polar tip (- ang (* 0.85 pi)) sz) col)
)

;; a circle of radius r around p (UCS) with 24 short lines
(defun ab:pls-circle (p r col / k)
  (setq k 0)
  (repeat 24
    (grdraw (polar p (* k (/ pi 12.0)) r) (polar p (* (1+ k) (/ pi 12.0)) r) col)
    (setq k (1+ k))
  )
)

(defun c:PLS (/ sel ent len sz n i dist par pt d ang p q r)
  (while (setq sel (entsel "\nSelect a polyline / line / arc (Enter = finish): "))
    (setq ent (car sel))
    (if (vl-catch-all-error-p (vl-catch-all-apply 'vlax-curve-getEndParam (list ent)))
      (princ "\n  That is not a line or a polyline.")
      (progn
        (setq len (vlax-curve-getDistAtParam ent (vlax-curve-getEndParam ent))
              sz  (/ (getvar "VIEWSIZE") 35.0)        ; marks sized to the screen
              n   10)                                  ; arrows along the curve
        ;; start: green circle
        (setq p (trans (vlax-curve-getStartPoint ent) 0 1))
        (ab:pls-circle p (* sz 0.7) 3)
        (ab:pls-circle p (* sz 0.45) 3)
        ;; arrows: the first one big and green, then red
        (setq i 0)
        (repeat (1+ n)
          (setq dist (* len (/ i (float n)))
                par  (vlax-curve-getParamAtDist ent dist)
                pt   (vlax-curve-getPointAtParam ent par)
                d    (vlax-curve-getFirstDeriv ent par)
                ang  (angle '(0 0 0) (trans d 0 1 T)))
          (if (= i 0)
            (ab:pls-arrow (vlax-curve-getPointAtDist ent (min len (* sz 2.0))) ang (* sz 1.5) 3)
            (ab:pls-arrow pt ang sz 1)
          )
          (setq i (1+ i))
        )
        ;; end: red square
        (setq q (trans (vlax-curve-getEndPoint ent) 0 1)
              r (* sz 0.5))
        (grdraw (list (- (car q) r) (- (cadr q) r)) (list (+ (car q) r) (- (cadr q) r)) 1)
        (grdraw (list (+ (car q) r) (- (cadr q) r)) (list (+ (car q) r) (+ (cadr q) r)) 1)
        (grdraw (list (+ (car q) r) (+ (cadr q) r)) (list (- (car q) r) (+ (cadr q) r)) 1)
        (grdraw (list (- (car q) r) (+ (cadr q) r)) (list (- (car q) r) (- (cadr q) r)) 1)
        (princ (strcat "\n  Length " (rtos len 2 3) "  -  start = green circle, direction = arrows, end = red square (zoom / pan clears them)."))
      )
    )
  )
  (princ)
)

(princ "\nAB Bridges: type PLS to see where a polyline starts (marks go away on zoom).")
(princ)
