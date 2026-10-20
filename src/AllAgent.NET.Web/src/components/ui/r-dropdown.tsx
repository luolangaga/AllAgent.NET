"use client";

import { Menu as MenuPrimitive } from "@base-ui/react/menu";
import { Check, ChevronDown } from "lucide-react";
import { motion, useReducedMotion } from "motion/react";
import * as React from "react";

import { cn } from "@/lib/utils";

const triggerClass =
  "flex min-h-10 w-full touch-manipulation items-center justify-between gap-2 rounded-lg border border-border bg-card px-3 py-2 text-left font-medium text-foreground text-sm transition-colors hover:bg-accent/60 focus:outline-none focus-visible:ring-2 focus-visible:ring-ring/50 disabled:cursor-not-allowed disabled:opacity-60";

const panelClass =
  "z-[300] min-w-[12rem] max-w-[calc(100vw-1.5rem)] overflow-hidden rounded-lg border border-border/70 bg-popover text-popover-foreground shadow-[0_18px_40px_-24px_rgba(15,23,42,0.45)] outline-none";

const itemClass =
  "relative flex min-h-9 w-full cursor-default select-none items-center justify-between gap-3 rounded-lg px-3 py-2 text-left text-foreground text-sm outline-none transition-colors data-[highlighted]:bg-accent data-[disabled]:pointer-events-none data-[disabled]:opacity-50";

const labelClass =
  "px-3 pt-1 pb-1 font-medium text-[10px] text-muted-foreground uppercase tracking-[0.14em]";

type DropdownContextValue = {
  labels: Record<string, string>;
  open: boolean;
  setValue: (value: string | undefined) => void;
  value: string | undefined;
};

const DropdownContext = React.createContext<DropdownContextValue | null>(null);

function useDropdown(componentName: string) {
  const context = React.useContext(DropdownContext);

  if (!context) {
    throw new Error(`${componentName} must be used within Dropdown.`);
  }

  return context;
}

function getText(node: React.ReactNode): string {
  if (typeof node === "string" || typeof node === "number") {
    return String(node);
  }

  if (Array.isArray(node)) {
    return node.map(getText).join("");
  }

  if (React.isValidElement<{ children?: React.ReactNode }>(node)) {
    return getText(node.props.children);
  }

  return "";
}

function collectLabels(
  node: React.ReactNode,
  labels: Record<string, string> = {}
) {
  React.Children.forEach(node, (child) => {
    if (!React.isValidElement(child)) {
      return;
    }

    const element = child as React.ReactElement<{
      children?: React.ReactNode;
      textValue?: string;
      value?: string;
    }>;
    const displayName = (element.type as { displayName?: string }).displayName;

    if (displayName === "DropdownItem" && element.props.value) {
      labels[element.props.value] = (
        element.props.textValue ?? getText(element.props.children)
      ).trim();
    }

    collectLabels(element.props.children, labels);
  });

  return labels;
}

export interface DropdownProps {
  children: React.ReactNode;
  className?: string;
  defaultOpen?: boolean;
  defaultValue?: string;
  disabled?: boolean;
  onOpenChange?: (open: boolean) => void;
  onValueChange?: (value: string | undefined) => void;
  open?: boolean;
  value?: string;
}

function Dropdown({
  children,
  className,
  defaultOpen = false,
  defaultValue,
  disabled = false,
  onOpenChange,
  onValueChange,
  open: openProp,
  value: valueProp,
}: DropdownProps) {
  const [uncontrolledOpen, setUncontrolledOpen] = React.useState(defaultOpen);
  const [uncontrolledValue, setUncontrolledValue] = React.useState<
    string | undefined
  >(defaultValue);
  const isOpenControlled = openProp !== undefined;
  const isValueControlled = valueProp !== undefined;
  const open = isOpenControlled ? openProp : uncontrolledOpen;
  const value = isValueControlled ? valueProp : uncontrolledValue;
  const labels = React.useMemo(() => collectLabels(children), [children]);

  const setValue = React.useCallback(
    (next: string | undefined) => {
      if (!isValueControlled) {
        setUncontrolledValue(next);
      }
      onValueChange?.(next);
    },
    [isValueControlled, onValueChange]
  );

  const handleOpenChange = React.useCallback(
    (next: boolean) => {
      if (!isOpenControlled) {
        setUncontrolledOpen(next);
      }
      onOpenChange?.(next);
    },
    [isOpenControlled, onOpenChange]
  );

  return (
    <MenuPrimitive.Root
      disabled={disabled}
      modal={false}
      onOpenChange={handleOpenChange}
      open={open}
    >
      <DropdownContext.Provider value={{ labels, open, setValue, value }}>
        <div className={cn("relative inline-block", className)}>{children}</div>
      </DropdownContext.Provider>
    </MenuPrimitive.Root>
  );
}

export interface DropdownTriggerProps {
  children?: React.ReactNode;
  className?: string;
  disabled?: boolean;
  showChevron?: boolean;
}

const DropdownTrigger = React.forwardRef<
  HTMLButtonElement,
  DropdownTriggerProps
>(
  (
    { children, className, disabled: disabledProp, showChevron = true },
    ref
  ) => {
    const { open } = useDropdown("DropdownTrigger");
    const reduceMotion = useReducedMotion() === true;

    return (
      <MenuPrimitive.Trigger
        className={cn(triggerClass, className)}
        disabled={disabledProp}
        ref={ref}
        type="button"
      >
        <span className="flex min-w-0 flex-1 items-center gap-2">{children}</span>
        {showChevron ? (
          <motion.span
            animate={{ rotate: open ? 180 : 0 }}
            className="shrink-0 text-muted-foreground"
            transition={
              reduceMotion
                ? { duration: 0 }
                : { type: "spring", stiffness: 360, damping: 32, mass: 0.82 }
            }
          >
            <ChevronDown className="size-4" />
          </motion.span>
        ) : null}
      </MenuPrimitive.Trigger>
    );
  }
);
DropdownTrigger.displayName = "DropdownTrigger";

export interface DropdownValueProps
  extends React.HTMLAttributes<HTMLSpanElement> {
  placeholder?: string;
}

const DropdownValue = React.forwardRef<HTMLSpanElement, DropdownValueProps>(
  ({ className, placeholder = "Select an option", ...props }, ref) => {
    const { labels, value } = useDropdown("DropdownValue");
    const label = value ? labels[value] ?? value : undefined;

    return (
      <span
        className={cn(
          "truncate",
          !label && "text-muted-foreground",
          className
        )}
        ref={ref}
        {...props}
      >
        {label ?? placeholder}
      </span>
    );
  }
);
DropdownValue.displayName = "DropdownValue";

export interface DropdownContentProps {
  align?: "start" | "center" | "end";
  children?: React.ReactNode;
  className?: string;
  side?: "top" | "bottom" | "left" | "right";
  sideOffset?: number;
}

const DropdownContent = React.forwardRef<
  HTMLDivElement,
  DropdownContentProps
>(({ align = "start", children, className, side = "bottom", sideOffset = 8 }, ref) => {
  const reduceMotion = useReducedMotion() === true;

  return (
    <MenuPrimitive.Portal>
      <MenuPrimitive.Positioner
        align={align}
        className="z-[300] outline-none"
        collisionPadding={12}
        side={side}
        sideOffset={sideOffset}
      >
        <MenuPrimitive.Popup
          className={cn(panelClass, className)}
          ref={ref}
          style={{ width: "var(--anchor-width)" }}
        >
          <motion.div
            animate={{ opacity: 1, scale: 1, y: 0 }}
            className="max-h-[min(70vh,420px)] overflow-y-auto p-1.5"
            initial={
              reduceMotion ? false : { opacity: 0, scale: 0.985, y: -4 }
            }
            transition={
              reduceMotion
                ? { duration: 0 }
                : { type: "spring", stiffness: 260, damping: 32, mass: 0.95 }
            }
          >
            {children}
          </motion.div>
        </MenuPrimitive.Popup>
      </MenuPrimitive.Positioner>
    </MenuPrimitive.Portal>
  );
});
DropdownContent.displayName = "DropdownContent";

export interface DropdownItemProps {
  children?: React.ReactNode;
  className?: string;
  closeOnClick?: boolean;
  disabled?: boolean;
  onClick?: React.MouseEventHandler<HTMLDivElement>;
  textValue?: string;
  value?: string;
}

function DropdownItem({
  children,
  className,
  closeOnClick = true,
  disabled,
  onClick,
  textValue,
  value,
}: DropdownItemProps) {
  const { setValue, value: selected } = useDropdown("DropdownItem");
  const label = (textValue ?? getText(children)).trim();
  const isSelected = value !== undefined && selected === value;

  return (
    <MenuPrimitive.Item
      className={cn(itemClass, className)}
      closeOnClick={closeOnClick}
      disabled={disabled}
      label={label}
      onClick={(event) => {
        onClick?.(event);
        if (!event.defaultPrevented && value !== undefined) {
          setValue(value);
        }
      }}
    >
      <span className="flex min-w-0 flex-1 items-center gap-2 truncate">
        {children}
      </span>
      {isSelected ? (
        <Check className="size-4 shrink-0 text-foreground" />
      ) : null}
    </MenuPrimitive.Item>
  );
}
DropdownItem.displayName = "DropdownItem";

export interface DropdownGroupProps {
  children?: React.ReactNode;
  className?: string;
  label?: React.ReactNode;
}

function DropdownGroup({ children, className, label }: DropdownGroupProps) {
  return (
    <MenuPrimitive.Group className={cn("space-y-0.5", className)}>
      {label ? (
        <MenuPrimitive.GroupLabel className={labelClass}>
          {label}
        </MenuPrimitive.GroupLabel>
      ) : null}
      {children}
    </MenuPrimitive.Group>
  );
}
DropdownGroup.displayName = "DropdownGroup";

function DropdownSeparator({ className }: { className?: string }) {
  return (
    <MenuPrimitive.Separator
      className={cn("my-1 h-px bg-border/60", className)}
    />
  );
}
DropdownSeparator.displayName = "DropdownSeparator";

export {
  Dropdown,
  DropdownContent,
  DropdownGroup,
  DropdownItem,
  DropdownSeparator,
  DropdownTrigger,
  DropdownValue,
};
