import { Fragment, useEffect, useRef, useState } from 'react';
import { CheckIcon, ChevronDownIcon, XMarkIcon } from '@heroicons/react/24/outline';

export interface MultiSelectOption<TValue extends string | number> {
  value: TValue;
  label: string;
  hint?: string;
}

interface MultiSelectProps<TValue extends string | number> {
  options: MultiSelectOption<TValue>[];
  value: TValue[];
  onChange: (next: TValue[]) => void;
  placeholder?: string;
  className?: string;
  disabled?: boolean;
}

/**
 * Reusable multi-select dropdown with checkbox semantics. Selected
 * entries render as removable chips inside the trigger, and the popover
 * shows a checkbox list. Designed to replace the comma-separated text
 * inputs that previously stood in for multi-selects across the Indexer
 * and Media Management settings — those are too easy to misformat and
 * give the user no discoverability for valid values.
 *
 * Generic over TValue so number-keyed enums (FailDownloads,
 * MultiLanguages) and string-keyed enums (e.g. categories) both work
 * without value coercion at the call site.
 */
export function MultiSelect<TValue extends string | number>({
  options,
  value,
  onChange,
  placeholder = 'Select...',
  className,
  disabled,
}: MultiSelectProps<TValue>) {
  const [open, setOpen] = useState(false);
  const containerRef = useRef<HTMLDivElement>(null);

  // Close-on-outside-click. The popover is anchored to the trigger via
  // absolute positioning inside the same flex container, so we only
  // need to listen for clicks that fall outside the wrapper.
  useEffect(() => {
    if (!open) return;
    const handler = (e: MouseEvent) => {
      if (containerRef.current && !containerRef.current.contains(e.target as Node)) {
        setOpen(false);
      }
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, [open]);

  const isSelected = (val: TValue) => value.includes(val);

  const toggle = (val: TValue) => {
    if (disabled) return;
    if (isSelected(val)) {
      onChange(value.filter(v => v !== val));
    } else {
      onChange([...value, val]);
    }
  };

  const removeChip = (e: React.MouseEvent, val: TValue) => {
    e.stopPropagation();
    onChange(value.filter(v => v !== val));
  };

  const labelFor = (val: TValue) => options.find(o => o.value === val)?.label ?? String(val);

  return (
    <div ref={containerRef} className={`relative ${className ?? ''}`}>
      <button
        type="button"
        disabled={disabled}
        onClick={() => setOpen(o => !o)}
        className="w-full min-h-[42px] px-3 py-1.5 bg-gray-800 border border-gray-700 rounded-lg text-left text-white focus:outline-none focus:border-red-600 hover:border-gray-600 transition-colors flex items-center gap-2 flex-wrap disabled:opacity-50 disabled:cursor-not-allowed"
      >
        {value.length === 0 ? (
          <span className="text-gray-500 text-sm">{placeholder}</span>
        ) : (
          value.map(val => (
            <span
              key={String(val)}
              className="inline-flex items-center gap-1 px-2 py-0.5 bg-red-600/30 border border-red-600/50 rounded text-xs text-white"
            >
              {labelFor(val)}
              <button
                type="button"
                onClick={(e) => removeChip(e, val)}
                className="hover:text-red-300"
                aria-label={`Remove ${labelFor(val)}`}
              >
                <XMarkIcon className="w-3 h-3" />
              </button>
            </span>
          ))
        )}
        <ChevronDownIcon className={`w-4 h-4 text-gray-400 ml-auto flex-shrink-0 transition-transform ${open ? 'rotate-180' : ''}`} />
      </button>

      {open && (
        <div className="absolute z-20 mt-1 w-full max-h-60 overflow-y-auto bg-gray-900 border border-gray-700 rounded-lg shadow-xl">
          {options.length === 0 ? (
            <div className="px-3 py-2 text-sm text-gray-500">No options available</div>
          ) : (
            options.map(option => (
              <Fragment key={String(option.value)}>
                <button
                  type="button"
                  onClick={() => toggle(option.value)}
                  className="w-full px-3 py-2 text-left hover:bg-gray-800 transition-colors flex items-center gap-2"
                >
                  <span className={`w-4 h-4 rounded border flex items-center justify-center flex-shrink-0 ${
                    isSelected(option.value)
                      ? 'bg-red-600 border-red-600'
                      : 'bg-gray-800 border-gray-600'
                  }`}>
                    {isSelected(option.value) && <CheckIcon className="w-3 h-3 text-white" />}
                  </span>
                  <span className="flex-1 min-w-0">
                    <span className="block text-sm text-white truncate">{option.label}</span>
                    {option.hint && <span className="block text-xs text-gray-500 truncate">{option.hint}</span>}
                  </span>
                </button>
              </Fragment>
            ))
          )}
        </div>
      )}
    </div>
  );
}
